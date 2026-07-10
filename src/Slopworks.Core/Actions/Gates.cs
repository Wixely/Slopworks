using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Slopworks.Core.Actions;

/// <summary>Auto mode: approves everything. The command log still records each decision as "auto".</summary>
public sealed class AutoApproveGate(ILogger logger) : IActionGate
{
    public Task<ActionDecision> RequestAsync(PlannedAction action, CancellationToken ct)
    {
        logger.LogInformation("Auto-approved [{Kind}] {Description}: {Detail}", action.Kind, action.Description, action.Detail);
        return Task.FromResult(ActionDecision.Approved);
    }
}

public sealed record PendingApproval(PlannedAction Action, TaskCompletionSource<ActionDecision> Decision)
{
    public void Resolve(ActionDecision decision) => Decision.TrySetResult(decision);
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

    public async Task<ActionDecision> RequestAsync(PlannedAction action, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_approvedSteps.Contains(action.StepId))
                return ActionDecision.Approved;
        }

        var tcs = new TaskCompletionSource<ActionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _pending.Writer.WriteAsync(new PendingApproval(action, tcs), ct);

        await using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        var decision = await tcs.Task;

        if (decision == ActionDecision.ApprovedAllForStep)
        {
            lock (_lock)
                _approvedSteps.Add(action.StepId);
        }

        return decision;
    }
}

/// <summary>
/// Decorator that auto-approves file writes/deletes confined to the Slopworks root even in
/// safe mode (configurable) — without this, safe mode is unbearably chatty. Executions,
/// downloads, and anything outside the root always reach the inner gate.
/// </summary>
public sealed class PolicyGate(IActionGate inner, bool autoApproveInsideRoot) : IActionGate
{
    public Task<ActionDecision> RequestAsync(PlannedAction action, CancellationToken ct)
    {
        var isRootConfinedFileOp = action.InsideSlopworksRoot
            && action.Kind is ActionKind.WriteFile or ActionKind.DeleteFile;

        return autoApproveInsideRoot && isRootConfinedFileOp
            ? Task.FromResult(ActionDecision.Approved)
            : inner.RequestAsync(action, ct);
    }
}
