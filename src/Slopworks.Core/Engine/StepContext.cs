using Microsoft.Extensions.Logging;
using Slopworks.Core.Config;
using Slopworks.Core.State;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Core.Engine;

/// <summary>
/// Everything a step may consult during Detect/Plan/Verify. Deliberately does NOT expose a
/// way to perform side effects — those go through PlannedAction and the gate.
/// </summary>
public sealed class StepContext
{
    public required SlopworksPaths Paths { get; init; }
    public required SlopworksConfig Config { get; init; }
    public required SystemProfile Profile { get; init; }
    public required ILogger Logger { get; init; }
    public required IStateJournal Journal { get; init; }

    /// <summary>
    /// Runner for READ-ONLY detection probes (wsl --status, podman info, nvidia-smi --query, ...).
    /// Probes are logged but not gated; by contract they must not mutate anything.
    /// </summary>
    public required IProcessRunner Probes { get; init; }
}
