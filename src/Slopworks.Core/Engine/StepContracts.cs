using Slopworks.Core.Actions;

namespace Slopworks.Core.Engine;

public enum StepState
{
    Unknown,
    Missing,
    Partial,
    Broken,
    Ok,
}

public sealed record StepDetection(StepState State, string Summary, IReadOnlyList<string> Evidence)
{
    /// <summary>
    /// Set when this failure is an opinionated check (resource headroom etc.) rather than a
    /// technical requirement. The UI offers a one-click bypass that records the key in
    /// config.json "bypasses"; bypassed checks downgrade to warnings on the next detect.
    /// </summary>
    public string? BypassKey { get; init; }

    public static StepDetection Ok(string summary, params string[] evidence) => new(StepState.Ok, summary, evidence);
    public static StepDetection Missing(string summary, params string[] evidence) => new(StepState.Missing, summary, evidence);
    public static StepDetection Partial(string summary, params string[] evidence) => new(StepState.Partial, summary, evidence);
    public static StepDetection Broken(string summary, params string[] evidence) => new(StepState.Broken, summary, evidence);
}

/// <summary>
/// A convergent setup step. Detect reports the current state; Plan emits only the actions
/// needed to reach Ok from that state (an Ok detection must plan nothing — repair is just
/// planning against Partial/Broken). The engine executes planned actions itself, gating
/// each one, so steps never run side effects directly.
/// </summary>
public interface ISetupStep
{
    /// <summary>Stable identifier, used as journal key and dependency reference (e.g. "wsl.feature").</summary>
    string Id { get; }

    string Title { get; }

    IReadOnlyList<string> DependsOn { get; }

    /// <summary>False to skip entirely on this machine (e.g. GPU steps on a CPU-only box).</summary>
    bool AppliesTo(SystemProfile profile);

    Task<StepDetection> DetectAsync(StepContext ctx, CancellationToken ct);

    Task<IReadOnlyList<PlannedAction>> PlanAsync(StepContext ctx, StepDetection detection, CancellationToken ct);

    /// <summary>Post-apply re-detection. Defaults to DetectAsync.</summary>
    Task<StepDetection> VerifyAsync(StepContext ctx, CancellationToken ct) => DetectAsync(ctx, ct);
}
