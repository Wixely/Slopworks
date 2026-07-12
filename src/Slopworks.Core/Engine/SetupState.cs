using Slopworks.Core.State;

namespace Slopworks.Core.Engine;

/// <summary>
/// A fast, probe-free read of whether setup already finished, from the journal's record of
/// the final step. Used to decide the landing page without running (slow) live detection.
/// </summary>
public static class SetupState
{
    public const string FinalStepId = "vllm.smoke";

    public static bool IsComplete(IStateJournal journal)
        => journal.Data.Steps.TryGetValue(FinalStepId, out var entry)
        && string.Equals(entry.LastState, StepState.Ok.ToString(), StringComparison.Ordinal);
}
