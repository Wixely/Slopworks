using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slopworks.Core.Config;
using Slopworks.Core.Logging;
using Slopworks.Platform.Abstractions;

namespace Slopworks.App.ViewModels;

/// <summary>
/// Direct server control. Buttons here run commands immediately — the click is the consent —
/// but every command is still recorded in the audit log with attribution "server-ui".
/// </summary>
public partial class ServerViewModel(SlopworksHost host) : ObservableObject
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
    private bool _isBusy;

    [ObservableProperty]
    private bool _exposeToNetwork = host.Config.Server.ExposeToNetwork;

    [ObservableProperty]
    private string _networkStatus = "";

    /// <summary>OpenAI-compatible base URLs to point an agent/client at (local first, LAN when exposed).</summary>
    public ObservableCollection<string> AgentUrls { get; } = [$"{host.Server.BaseUrl}/v1"];

    public string AgentHint =>
        $"Point your agent's base_url at one of these. Model id: '{Model}'. Any API key is accepted (no auth).";

    private bool _applyingExposure;

    partial void OnModelChanged(string value) => OnPropertyChanged(nameof(AgentHint));

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

    [RelayCommand]
    private async Task TailLogsAsync()
        => await RunBusyAsync(async () =>
        {
            var logs = await host.Server.GetLogsAsync(Runner, 200, CancellationToken.None);
            Logs = logs.Stdout.Trim().Length > 0 ? logs.Stdout : "(no log output — is the container running?)";
        });

    /// <summary>WSL localhost forwarding sometimes breaks after host sleep; a WSL restart fixes it.</summary>
    [RelayCommand]
    private async Task RepairForwardingAsync()
        => await RunBusyAsync(async () =>
        {
            await Runner.RunAsync(
                new ProcessSpec("wsl.exe", ["--shutdown"], StdoutEncoding: System.Text.Encoding.Unicode),
                null, CancellationToken.None);
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
