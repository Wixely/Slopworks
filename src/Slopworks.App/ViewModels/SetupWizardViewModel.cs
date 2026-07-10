using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slopworks.Core.Actions;
using Slopworks.Core.Config;
using Slopworks.Core.Engine;

namespace Slopworks.App.ViewModels;

public partial class SetupWizardViewModel(SlopworksHost host) : ObservableObject
{
    private const int OutputCap = 1000;

    public ObservableCollection<StepStatusItemViewModel> Steps { get; } = [];
    public ObservableCollection<string> Output { get; } = [];

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "Ready. Setup converges the machine to a working vLLM stack; already-satisfied steps are skipped.";

    [ObservableProperty]
    private PendingApprovalViewModel? _currentApproval;

    [ObservableProperty]
    private bool _rebootRequired;

    [ObservableProperty]
    private bool _autoMode = host.Config.IsAutoMode;

    private CancellationTokenSource? _cts;

    partial void OnAutoModeChanged(bool value)
    {
        host.Config.Mode = value ? "auto" : "safe";
        ConfigStore.Save(host.Paths, host.Config);
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning)
            return;

        IsRunning = true;
        RebootRequired = false;
        Output.Clear();
        StatusText = "Running…";
        _cts = new CancellationTokenSource();

        try
        {
            var (engine, profile) = await host.CreateEngineAsync(_cts.Token);

            Steps.Clear();
            var items = new Dictionary<string, StepStatusItemViewModel>();
            foreach (var step in engine.Steps.Where(s => s.AppliesTo(profile)))
            {
                var item = new StepStatusItemViewModel(step.Id, step.Title);
                items[step.Id] = item;
                Steps.Add(item);
            }

            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var approvals = host.InteractiveGate is { } gate
                ? DrainApprovalsAsync(gate, drainCts.Token)
                : Task.CompletedTask;

            var progress = new InlineProgress<EngineEvent>(e =>
                Dispatcher.UIThread.Post(() => HandleEvent(e, items)));

            var result = await Task.Run(() => engine.ConvergeAsync(progress, _cts.Token));

            drainCts.Cancel();
            try
            {
                await approvals;
            }
            catch (OperationCanceledException)
            {
            }

            StatusText = result.Status switch
            {
                RunStatus.Converged => "Setup complete — everything converged.",
                RunStatus.RebootRequired => "Windows must restart to continue. Re-run setup after rebooting.",
                RunStatus.Aborted => "Run aborted.",
                RunStatus.Cancelled => "Run cancelled.",
                _ => $"Setup stopped at '{result.FailedStepId}': {result.Detail}",
            };
        }
        catch (OperationCanceledException)
        {
            StatusText = "Run cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Unexpected error: {ex.Message}";
        }
        finally
        {
            CurrentApproval = null;
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>User clicked "Restart now" on the reboot banner — that click is the consent.</summary>
    [RelayCommand]
    private async Task RestartNowAsync()
    {
        host.ShellIntegration.InstallResumeOnStartup();
        StatusText = "Restarting Windows in a few seconds… Slopworks reopens after login.";
        await host.ShellIntegration.RequestRebootAsync(CancellationToken.None);
    }

    private async Task DrainApprovalsAsync(InteractiveGate gate, CancellationToken ct)
    {
        await foreach (var pending in gate.Pending.ReadAllAsync(ct))
        {
            var decided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(() =>
                CurrentApproval = new PendingApprovalViewModel(pending, () =>
                {
                    CurrentApproval = null;
                    decided.TrySetResult();
                }));

            await using var reg = ct.Register(() => decided.TrySetCanceled(ct));
            await decided.Task;
        }
    }

    private void HandleEvent(EngineEvent e, Dictionary<string, StepStatusItemViewModel> items)
    {
        switch (e)
        {
            case EngineEvent.StepStarted started when items.TryGetValue(started.StepId, out var item):
                item.Summary = "Working…";
                break;

            case EngineEvent.StepDetected detected when items.TryGetValue(detected.StepId, out var item):
                item.Update(detected.Detection);
                break;

            case EngineEvent.ActionPending pending:
                Append($"▶ {pending.Action.Description}");
                Append($"    {pending.Action.Detail}");
                break;

            case EngineEvent.ActionOutput output:
                Append(output.Line);
                break;

            case EngineEvent.ActionCompleted completed:
                Append(completed.Result.Succeeded
                    ? $"✓ {completed.Result.Detail ?? "done"}"
                    : $"✗ {completed.Result.Detail ?? "failed"}");
                break;

            case EngineEvent.StepCompleted stepDone when items.TryGetValue(stepDone.StepId, out var item):
                item.MarkOutcome(stepDone.Outcome, stepDone.Detail);
                break;

            case EngineEvent.RebootRequired:
                RebootRequired = true;
                break;
        }
    }

    private void Append(string line)
    {
        Output.Add(line);
        while (Output.Count > OutputCap)
            Output.RemoveAt(0);
    }
}
