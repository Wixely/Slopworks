using Slopworks.Core.Actions;

namespace Slopworks.Core.Engine;

public enum StepOutcome
{
    Ok,
    Skipped,
    Failed,
    Denied,
    RebootRequired,
}

/// <summary>
/// The single event stream the UI binds to. Emitted via IProgress&lt;EngineEvent&gt; from
/// whatever thread the engine runs on — consumers marshal to their own context.
/// </summary>
public abstract record EngineEvent
{
    public sealed record RunStarted(string RunId, IReadOnlyList<string> StepIds) : EngineEvent;

    public sealed record StepStarted(string StepId, string Title) : EngineEvent;

    public sealed record StepDetected(string StepId, StepDetection Detection) : EngineEvent;

    public sealed record ActionPending(PlannedAction Action) : EngineEvent;

    public sealed record ActionDecided(string ActionId, ActionDecision Decision) : EngineEvent;

    public sealed record ActionOutput(string StepId, string ActionId, string Line) : EngineEvent;

    public sealed record ActionCompleted(string ActionId, ActionResult Result) : EngineEvent;

    public sealed record StepCompleted(string StepId, StepOutcome Outcome, string? Detail = null) : EngineEvent;

    public sealed record RebootRequired(string StepId) : EngineEvent;

    public sealed record RunCompleted(ConvergeResult Result) : EngineEvent;
}

public enum RunStatus
{
    Converged,
    Failed,
    Aborted,
    Cancelled,
    RebootRequired,
}

public sealed record ConvergeResult(RunStatus Status, string? FailedStepId = null, string? Detail = null);
