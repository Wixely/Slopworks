namespace Slopworks.Core.Actions;

public enum ActionDecision
{
    Approved,

    /// <summary>Approve this and every subsequent action belonging to the same step.</summary>
    ApprovedAllForStep,

    /// <summary>Skip this action; the owning step is marked failed and the run halts.</summary>
    Denied,

    /// <summary>Stop the entire run immediately.</summary>
    Aborted,
}

/// <summary>
/// Every side effect passes through a gate before executing. Safe mode routes each action
/// to the user; auto mode approves everything (still logged).
/// </summary>
public interface IActionGate
{
    Task<ActionDecision> RequestAsync(PlannedAction action, CancellationToken ct);
}
