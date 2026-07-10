using Microsoft.Extensions.Logging;
using Slopworks.Core.Config;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Core.Actions;

public enum ActionKind
{
    Execute,
    ExecuteElevated,
    Download,
    WriteFile,
    DeleteFile,
    Reboot,
}

public sealed record ActionResult(bool Succeeded, bool RebootRequired = false, string? Detail = null)
{
    public static ActionResult Success(string? detail = null) => new(true, Detail: detail);
    public static ActionResult NeedsReboot(string? detail = null) => new(true, RebootRequired: true, Detail: detail);
    public static ActionResult Failure(string detail) => new(false, Detail: detail);
}

/// <summary>
/// Everything an approved action may touch. Steps cannot reach IProcessRunner or the
/// filesystem helpers except through this context, which the engine hands to the action's
/// Execute delegate only after the gate approves — bypassing the gate is a compile-time
/// inconvenience, not a code-review hope.
/// </summary>
public sealed class ActionExecutionContext
{
    public required IProcessRunner Processes { get; init; }
    public required ILogger Logger { get; init; }
    public required SlopworksPaths Paths { get; init; }

    /// <summary>Live output lines, surfaced in the UI's streaming pane.</summary>
    public required IProgress<string> Output { get; init; }
}

/// <summary>
/// A single side effect a step wants to perform. Description is the human summary;
/// Detail is the verbatim command line / URL / file path, shown unedited on the
/// confirmation card in safe mode.
/// </summary>
public sealed record PlannedAction(
    string ActionId,
    string StepId,
    ActionKind Kind,
    string Description,
    string Detail,
    bool InsideSlopworksRoot,
    Func<ActionExecutionContext, CancellationToken, Task<ActionResult>> Execute);
