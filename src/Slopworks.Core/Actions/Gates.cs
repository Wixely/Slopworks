using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Slopworks.Core.Actions;

/// <summary>Auto mode: approves everything with the default choice. Still logged as "auto".</summary>
public sealed class AutoApproveGate(ILogger logger) : IActionGate
{
    public Task<GateResult> RequestAsync(PlannedAction action, CancellationToken ct)
    {
        logger.LogInformation("Auto-approved [{Kind}] {Description}: {Detail}", action.Kind, action.Description, action.Detail);
        return Task.FromResult(GateResult.Approved);
    }
}

public sealed record PendingApproval(PlannedAction Action, TaskCompletionSource<GateResult> Decision)
{
    public void Resolve(ActionDecision decision) => Decision.TrySetResult(new GateResult(decision));

    /// <summary>Approve via a specific choice (index into Action.Choices).</summary>
    public void ResolveChoice(int choiceIndex)
        => Decision.TrySetResult(new GateResult(ActionDecision.Approved, choiceIndex));
}

/// <summary>
/// Safe mode: posts each action to a channel the UI drains, then awaits the user's decision.
/// The engine task pauses naturally on the await. Remembers "approve all" per step.
/// </summary>
public sealed class InteractiveGate : IActionGate
{
    private readonly Channel<PendingApproval> _pending = Channel.CreateUnbounded<PendingApproval>();
    private readonly HashSet<string> _approvedSteps = [];
    private readonly Lock _lock = new();

    public ChannelReader<PendingApproval> Pending => _pending.Reader;

    public async Task<GateResult> RequestAsync(PlannedAction action, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_approvedSteps.Contains(action.StepId))
                return GateResult.Approved;
        }

        var tcs = new TaskCompletionSource<GateResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _pending.Writer.WriteAsync(new PendingApproval(action, tcs), ct);

        await using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        var result = await tcs.Task;

        if (result.Decision == ActionDecision.ApprovedAllForStep)
        {
            lock (_lock)
                _approvedSteps.Add(action.StepId);
        }

        return result;
    }
}

/// <summary>
/// Decorator that auto-approves file writes/deletes confined to the Slopworks root even in
/// safe mode (configurable) — without this, safe mode is unbearably chatty. Executions,
/// downloads, and anything outside the root always reach the inner gate.
/// </summary>
public sealed class PolicyGate(IActionGate inner, bool autoApproveInsideRoot) : IActionGate
{
    public Task<GateResult> RequestAsync(PlannedAction action, CancellationToken ct)
    {
        var isRootConfinedFileOp = action.InsideSlopworksRoot
            && action.Kind is ActionKind.WriteFile or ActionKind.DeleteFile;

        return autoApproveInsideRoot && isRootConfinedFileOp
            ? Task.FromResult(GateResult.Approved)
            : inner.RequestAsync(action, ct);
    }
}
