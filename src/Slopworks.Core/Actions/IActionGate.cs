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

/// <summary>Gate verdict; ChoiceIndex selects among PlannedAction.Choices (0 = default).</summary>
public sealed record GateResult(ActionDecision Decision, int ChoiceIndex = 0)
{
    public static GateResult Approved { get; } = new(ActionDecision.Approved);
}

/// <summary>
/// Every side effect passes through a gate before executing. Safe mode routes each action
/// to the user; auto mode approves everything with the default choice (still logged).
/// </summary>
public interface IActionGate
{
    Task<GateResult> RequestAsync(PlannedAction action, CancellationToken ct);
}
