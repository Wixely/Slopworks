using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slopworks.Core;
using Slopworks.Core.Config;
using Slopworks.Core.Logging;
using Slopworks.Core.Server;
using Slopworks.Platform.Abstractions;

namespace Slopworks.App.ViewModels;

/// <summary>
/// Direct server control. Buttons here run commands immediately — the click is the consent —
/// but every command is still recorded in the audit log with attribution "server-ui".
/// </summary>
public partial class ServerViewModel(SlopworksHost host) : ObservableObject, IActivatableTab
{
    [ObservableProperty]
    private string _model = host.Config.Server.Model;

    [ObservableProperty]
    private string _hfToken = host.Config.Server.HfToken ?? "";

    [ObservableProperty]
    private string _statusText = "Press Refresh to query the server state.";

    [ObservableProperty]
    private string _logs = "";

    [ObservableProperty]
    private bool _isLiveLogging;

    [ObservableProperty]
    private bool _isBusy;

    private DispatcherTimer? _logTimer;
    private bool _pollInFlight;

    public string LiveLogsLabel => IsLiveLogging ? "Stop live logs" : "Live logs";

    partial void OnIsLiveLoggingChanged(bool value) => OnPropertyChanged(nameof(LiveLogsLabel));

    [ObservableProperty]
    private bool _exposeToNetwork = host.Config.Server.ExposeToNetwork;

    [ObservableProperty]
    private string _networkStatus = "";

    /// <summary>OpenAI-compatible base URLs to point an agent/client at (local first, LAN when exposed).</summary>
    public ObservableCollection<string> AgentUrls { get; } = [$"{host.Server.BaseUrl}/v1"];

    public string AgentHint =>
        $"Point your agent's base_url at one of these. Model id: '{Model}'. Any API key is accepted (no auth).";

    /// <summary>A caution shown under the model box when the id looks Ollama/GGUF-shaped.</summary>
    public string? ModelAdvisory => ModelId.Advisory(Model);
    public bool HasModelAdvisory => ModelAdvisory is not null;

    private bool _applyingExposure;

    partial void OnModelChanged(string value)
    {
        OnPropertyChanged(nameof(AgentHint));
        OnPropertyChanged(nameof(ModelAdvisory));
        OnPropertyChanged(nameof(HasModelAdvisory));
        // The previous check result is about a different id now.
        HasModelCheck = false;
        ModelCheckText = "";
    }

    // Result of the "Check" button — does HuggingFace show a repo vLLM can actually serve?
    [ObservableProperty]
    private string _modelCheckText = "";

    [ObservableProperty]
    private bool _hasModelCheck;

    [ObservableProperty]
    private IBrush _modelCheckBrush = Brushes.Gray;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckModelCommand))]
    private bool _isCheckingModel;

    private bool CanCheckModel => !IsCheckingModel;

    [RelayCommand(CanExecute = nameof(CanCheckModel))]
    private async Task CheckModelAsync()
    {
        IsCheckingModel = true;
        HasModelCheck = true;
        ModelCheckBrush = Brushes.Gray;
        ModelCheckText = $"Checking {Model.Trim()} on HuggingFace…";
        try
        {
            var result = await host.ModelInspector.InspectAsync(Model, CancellationToken.None);
            ModelCheckText = $"{result.Summary} — {result.Detail}";
            ModelCheckBrush = BrushFor(result.Verdict);
        }
        catch (Exception ex)
        {
            ModelCheckText = $"Check failed: {ex.Message}";
            ModelCheckBrush = Brushes.Gray;
        }
        finally
        {
            IsCheckingModel = false;
        }
    }

    private static IBrush BrushFor(ModelVerdict verdict) => verdict switch
    {
        ModelVerdict.Servable => new SolidColorBrush(Color.Parse("#3FB950")),   // green
        ModelVerdict.Caution => new SolidColorBrush(Color.Parse("#E0A030")),    // amber
        ModelVerdict.Unservable => new SolidColorBrush(Color.Parse("#F0603E")), // red
        _ => new SolidColorBrush(Color.Parse("#9AA0A6")),                        // gray
    };

    private IProcessRunner Runner => new RecordingProcessRunner(
        host.ProcessRunner, host.CommandLog, "server-ui", "user-click");

    /// <summary>The toggle click is the consent; the commands are still elevated (UAC) and audited.</summary>
    partial void OnExposeToNetworkChanged(bool value)
    {
        if (_applyingExposure)
            return;

        _ = ApplyExposureAsync(value);
    }

    private async Task ApplyExposureAsync(bool enable)
    {
        var port = host.Config.Server.Port;
        try
        {
            var result = enable
                ? await host.NetworkExposure.EnableAsync(Runner, port, CancellationToken.None)
                : await host.NetworkExposure.DisableAsync(Runner, port, CancellationToken.None);

            if (!result.Succeeded && enable)
            {
                // Roll the toggle back without re-triggering the handler.
                _applyingExposure = true;
                ExposeToNetwork = false;
                _applyingExposure = false;
                NetworkStatus = $"Could not open port {port} to the network: " +
                    Slopworks.Core.TextUtil.Condense(result.Stderr + result.Stdout, 200);
                return;
            }

            host.Config.Server.ExposeToNetwork = enable;
            ConfigStore.Save(host.Paths, host.Config);
            UpdateNetworkStatus(enable, port);
        }
        catch (Exception ex)
        {
            NetworkStatus = $"Network exposure change failed: {ex.Message}";
        }
    }

    private void UpdateNetworkStatus(bool enabled, int port)
    {
        AgentUrls.Clear();
        AgentUrls.Add($"http://localhost:{port}/v1");

        if (!enabled)
        {
            NetworkStatus = "Server is reachable from this machine only (localhost).";
            return;
        }

        var addresses = host.NetworkExposure.GetLanAddresses();
        foreach (var address in addresses)
            AgentUrls.Add($"http://{address}:{port}/v1");

        NetworkStatus = addresses.Count > 0
            ? "Reachable from other machines on your network — no authentication, trusted networks only."
            : $"Port {port} is open to the network, but no LAN address was found to display.";
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        SaveConfig();
        await RunBusyAsync(async () =>
        {
            var profile = await host.SystemInfo.GetProfileAsync(CancellationToken.None);
            var result = await host.Server.StartAsync(Runner, profile, Model, null, CancellationToken.None);
            StatusText = result.Succeeded
                ? "Container started — first model load can take several minutes. Refresh to watch it come up."
                : $"Start failed: {(result.Stderr + result.Stdout).Trim()}";
        });
    }

    [RelayCommand]
    private async Task StopAsync()
        => await RunBusyAsync(async () =>
        {
            await host.Server.StopAsync(Runner, null, CancellationToken.None);
            StatusText = "Server stopped.";
        });

    [RelayCommand]
    private async Task RefreshAsync()
        => await RunBusyAsync(async () =>
        {
            var health = await host.Server.GetHealthAsync(Runner, CancellationToken.None);
            StatusText = health.ApiHealthy
                ? $"Container {health.ContainerState} · OpenAI-compatible API healthy at {host.Server.BaseUrl}/v1"
                : $"Container {health.ContainerState} · API not responding at {host.Server.BaseUrl}";

            // Reconcile the toggle with the actual portproxy state (read-only probe, no UAC).
            var actuallyEnabled = await host.NetworkExposure.IsEnabledAsync(
                Runner, host.Config.Server.Port, CancellationToken.None);
            _applyingExposure = true;
            ExposeToNetwork = actuallyEnabled;
            _applyingExposure = false;
            UpdateNetworkStatus(actuallyEnabled, host.Config.Server.Port);
        });

    /// <summary>Toggles a live tail that polls the container log every 2s and persists it.</summary>
    [RelayCommand]
    private void ToggleLiveLogs()
    {
        if (IsLiveLogging)
        {
            StopLiveLogs();
            return;
        }

        IsLiveLogging = true;
        _logTimer ??= CreateLogTimer();
        _ = PollLogsAsync(); // fetch immediately, then on each tick
        _logTimer.Start();
    }

    private DispatcherTimer CreateLogTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => _ = PollLogsAsync();
        return timer;
    }

    private void StopLiveLogs()
    {
        _logTimer?.Stop();
        IsLiveLogging = false;
    }

    private async Task PollLogsAsync()
    {
        if (_pollInFlight)
            return;

        _pollInFlight = true;
        try
        {
            var text = await host.Server.SnapshotLogsAsync(Runner, 500, CancellationToken.None);
            Logs = string.IsNullOrWhiteSpace(text) ? "(no log output — is the container running?)" : text;
        }
        catch (Exception ex)
        {
            Logs = $"(log fetch failed: {ex.Message})";
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    // IActivatableTab: stop polling when the user leaves the Server tab.
    public void Activate()
    {
    }

    public void Deactivate() => StopLiveLogs();

    /// <summary>WSL localhost forwarding sometimes breaks after host sleep; a WSL restart fixes it.</summary>
    [RelayCommand]
    private async Task RepairForwardingAsync()
        => await RunBusyAsync(async () =>
        {
            await Runner.RunAsync(
                Slopworks.Core.Steps.WslCommands.Management(["--shutdown"]), null, CancellationToken.None);
            StatusText = "WSL restarted (this stops the server too). Start the server again.";
        });

    private void SaveConfig()
    {
        host.Config.Server.Model = Model.Trim();
        host.Config.Server.HfToken = string.IsNullOrWhiteSpace(HfToken) ? null : HfToken.Trim();
        ConfigStore.Save(host.Paths, host.Config);
    }

    private async Task RunBusyAsync(Func<Task> work)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
