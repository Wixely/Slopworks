using Microsoft.Extensions.Logging;
using Slopworks.Core.Actions;
using Slopworks.Core.Logging;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Core.Engine;

public sealed class EngineServices
{
    public required StepContext StepContext { get; init; }
    public required IActionGate Gate { get; init; }
    public required IProcessRunner ProcessRunner { get; init; }
    public required ICommandLog CommandLog { get; init; }
    public required ILogger Logger { get; init; }
}

/// <summary>
/// Orchestrates setup steps: topologically sorts them, then per step runs
/// Detect → (skip if Ok) → Plan → gate each action → execute → Verify.
/// Repair is the same loop — Plan sees Partial/Broken and emits only corrective actions.
/// </summary>
public sealed class ConvergenceEngine
{
    private readonly IReadOnlyList<ISetupStep> _steps;
    private readonly EngineServices _services;

    public ConvergenceEngine(IEnumerable<ISetupStep> steps, EngineServices services)
    {
        _steps = TopologicalSort(steps.ToList());
        _services = services;
    }

    public IReadOnlyList<ISetupStep> Steps => _steps;

    /// <summary>Read-only refresh of every applicable step's state. No gate, no side effects.</summary>
    public async Task<IReadOnlyDictionary<string, StepDetection>> DetectAllAsync(
        IProgress<EngineEvent>? progress, CancellationToken ct)
    {
        var results = new Dictionary<string, StepDetection>();
        foreach (var step in _steps)
        {
            ct.ThrowIfCancellationRequested();
            if (!step.AppliesTo(_services.StepContext.Profile))
                continue;

            var detection = await DetectSafelyAsync(step, ct);
            results[step.Id] = detection;
            progress?.Report(new EngineEvent.StepDetected(step.Id, detection));
        }

        return results;
    }

    /// <summary>Converges every applicable step, or only the chain up to <paramref name="targetStepId"/>.</summary>
    public async Task<ConvergeResult> ConvergeAsync(
        IProgress<EngineEvent>? progress, CancellationToken ct, string? targetStepId = null)
    {
        var steps = targetStepId is null ? _steps : ChainTo(targetStepId);
        return await RunAsync(steps, progress, ct);
    }

    /// <summary>
    /// Repairs a single step from the dashboard. Its dependencies are detect-checked first;
    /// a non-Ok dependency fails the run with guidance rather than silently converging it.
    /// </summary>
    public async Task<ConvergeResult> ConvergeSingleAsync(
        string stepId, IProgress<EngineEvent>? progress, CancellationToken ct)
    {
        var step = _steps.FirstOrDefault(s => s.Id == stepId)
            ?? throw new ArgumentException($"Unknown step '{stepId}'.", nameof(stepId));

        foreach (var depId in TransitiveDependencies(step))
        {
            var dep = _steps.First(s => s.Id == depId);
            if (!dep.AppliesTo(_services.StepContext.Profile))
                continue;

            var detection = await DetectSafelyAsync(dep, ct);
            if (detection.State != StepState.Ok)
            {
                var result = new ConvergeResult(RunStatus.Failed, stepId,
                    $"Dependency '{depId}' is {detection.State}: {detection.Summary}. Repair it first or run a full setup.");
                progress?.Report(new EngineEvent.RunCompleted(result));
                return result;
            }
        }

        return await RunAsync([step], progress, ct);
    }

    private async Task<ConvergeResult> RunAsync(
        IReadOnlyList<ISetupStep> steps, IProgress<EngineEvent>? progress, CancellationToken ct)
    {
        var journal = _services.StepContext.Journal;
        journal.Data.RunId = $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH-mm-ssZ}-{Guid.NewGuid().ToString("N")[..6]}";
        progress?.Report(new EngineEvent.RunStarted(journal.Data.RunId, steps.Select(s => s.Id).ToList()));

        ConvergeResult result;
        try
        {
            result = await RunStepsAsync(steps, progress, ct);
        }
        catch (OperationCanceledException)
        {
            result = new ConvergeResult(RunStatus.Cancelled);
        }

        await journal.SaveAsync(CancellationToken.None);
        progress?.Report(new EngineEvent.RunCompleted(result));
        return result;
    }

    private async Task<ConvergeResult> RunStepsAsync(
        IReadOnlyList<ISetupStep> steps, IProgress<EngineEvent>? progress, CancellationToken ct)
    {
        var ctx = _services.StepContext;

        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();

            if (!step.AppliesTo(ctx.Profile))
            {
                progress?.Report(new EngineEvent.StepCompleted(step.Id, StepOutcome.Skipped, "Not applicable to this machine"));
                continue;
            }

            progress?.Report(new EngineEvent.StepStarted(step.Id, step.Title));
            var detection = await step.DetectAsync(ctx, ct);
            progress?.Report(new EngineEvent.StepDetected(step.Id, detection));

            if (detection.State == StepState.Ok)
            {
                RecordStepState(step.Id, detection);
                progress?.Report(new EngineEvent.StepCompleted(step.Id, StepOutcome.Ok, detection.Summary));
                continue;
            }

            var plan = await step.PlanAsync(ctx, detection, ct);
            if (plan.Count == 0)
            {
                var detail = $"Step is {detection.State} but planned no corrective actions ({detection.Summary}).";
                progress?.Report(new EngineEvent.StepCompleted(step.Id, StepOutcome.Failed, detail));
                return new ConvergeResult(RunStatus.Failed, step.Id, detail);
            }

            foreach (var action in plan)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new EngineEvent.ActionPending(action));

                var gateResult = await _services.Gate.RequestAsync(action, ct);
                var decision = gateResult.Decision;
                progress?.Report(new EngineEvent.ActionDecided(action.ActionId, decision));

                switch (decision)
                {
                    case ActionDecision.Aborted:
                        progress?.Report(new EngineEvent.StepCompleted(step.Id, StepOutcome.Failed, "Run aborted by user"));
                        return new ConvergeResult(RunStatus.Aborted, step.Id, $"Aborted at action: {action.Description}");

                    case ActionDecision.Denied:
                        progress?.Report(new EngineEvent.StepCompleted(step.Id, StepOutcome.Denied, $"Denied: {action.Description}"));
                        return new ConvergeResult(RunStatus.Failed, step.Id, $"Action denied: {action.Description}");
                }

                var actionResult = await ExecuteActionAsync(step, action, gateResult, progress, ct);
                progress?.Report(new EngineEvent.ActionCompleted(action.ActionId, actionResult));

                if (!actionResult.Succeeded)
                {
                    var detail = $"Action failed: {action.Description}. {actionResult.Detail}".TrimEnd();
                    progress?.Report(new EngineEvent.StepCompleted(step.Id, StepOutcome.Failed, detail));
                    return new ConvergeResult(RunStatus.Failed, step.Id, detail);
                }

                if (actionResult.RebootRequired)
                {
                    var journal = _services.StepContext.Journal;
                    journal.Data.PendingReboot = new State.PendingReboot
                    {
                        AfterStep = step.Id,
                        RequestedAt = DateTimeOffset.UtcNow,
                    };
                    await journal.SaveAsync(CancellationToken.None);
                    progress?.Report(new EngineEvent.RebootRequired(step.Id));
                    progress?.Report(new EngineEvent.StepCompleted(step.Id, StepOutcome.RebootRequired, actionResult.Detail));
                    return new ConvergeResult(RunStatus.RebootRequired, step.Id, actionResult.Detail);
                }
            }

            var verified = await step.VerifyAsync(ctx, ct);
            progress?.Report(new EngineEvent.StepDetected(step.Id, verified));
            RecordStepState(step.Id, verified);

            if (verified.State != StepState.Ok)
            {
                var detail = $"Verification after apply left step {verified.State}: {verified.Summary}";
                progress?.Report(new EngineEvent.StepCompleted(step.Id, StepOutcome.Failed, detail));
                return new ConvergeResult(RunStatus.Failed, step.Id, detail);
            }

            progress?.Report(new EngineEvent.StepCompleted(step.Id, StepOutcome.Ok, verified.Summary));
        }

        return new ConvergeResult(RunStatus.Converged);
    }

    private async Task<ActionResult> ExecuteActionAsync(
        ISetupStep step, PlannedAction action, GateResult gateResult,
        IProgress<EngineEvent>? progress, CancellationToken ct)
    {
        var decisionLabel = gateResult.Decision switch
        {
            ActionDecision.ApprovedAllForStep => "approved-all",
            _ when _services.Gate is AutoApproveGate => "auto",
            _ when action.Choices is { Count: > 0 } => $"approved-choice-{gateResult.ChoiceIndex}",
            _ => "approved",
        };

        var execCtx = new ActionExecutionContext
        {
            Processes = new RecordingProcessRunner(_services.ProcessRunner, _services.CommandLog, action.ActionId, decisionLabel),
            Logger = _services.Logger,
            Paths = _services.StepContext.Paths,
            Output = new InlineProgress<string>(line =>
                progress?.Report(new EngineEvent.ActionOutput(step.Id, action.ActionId, line))),
        };

        try
        {
            return await action.ResolveExecute(gateResult.ChoiceIndex)(execCtx, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _services.Logger.LogError(ex, "Action {ActionId} threw", action.ActionId);
            return ActionResult.Failure(ex.Message);
        }
    }

    private void RecordStepState(string stepId, StepDetection detection)
    {
        _services.StepContext.Journal.Data.Steps[stepId] = new State.StepJournalEntry
        {
            LastState = detection.State.ToString(),
            LastVerifiedAt = DateTimeOffset.UtcNow,
        };
    }

    private async Task<StepDetection> DetectSafelyAsync(ISetupStep step, CancellationToken ct)
    {
        try
        {
            return await step.DetectAsync(_services.StepContext, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _services.Logger.LogError(ex, "Detection for {StepId} threw", step.Id);
            return new StepDetection(StepState.Unknown, $"Detection failed: {ex.Message}", []);
        }
    }

    private IReadOnlyList<ISetupStep> ChainTo(string targetStepId)
    {
        var target = _steps.FirstOrDefault(s => s.Id == targetStepId)
            ?? throw new ArgumentException($"Unknown step '{targetStepId}'.", nameof(targetStepId));

        var wanted = TransitiveDependencies(target).Append(target.Id).ToHashSet();
        return _steps.Where(s => wanted.Contains(s.Id)).ToList();
    }

    private IEnumerable<string> TransitiveDependencies(ISetupStep step)
    {
        var byId = _steps.ToDictionary(s => s.Id);
        var seen = new HashSet<string>();
        var stack = new Stack<string>(step.DependsOn);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id))
                continue;
            foreach (var dep in byId[id].DependsOn)
                stack.Push(dep);
        }

        // Yield in engine (topological) order for deterministic checking.
        return _steps.Where(s => seen.Contains(s.Id)).Select(s => s.Id);
    }

    private static List<ISetupStep> TopologicalSort(List<ISetupStep> steps)
    {
        var byId = new Dictionary<string, ISetupStep>();
        foreach (var step in steps)
        {
            if (!byId.TryAdd(step.Id, step))
                throw new InvalidOperationException($"Duplicate step id '{step.Id}'.");
        }

        foreach (var step in steps)
        {
            foreach (var dep in step.DependsOn)
            {
                if (!byId.ContainsKey(dep))
                    throw new InvalidOperationException($"Step '{step.Id}' depends on unknown step '{dep}'.");
            }
        }

        // Kahn's algorithm, preserving registration order among ready steps.
        var remainingDeps = steps.ToDictionary(s => s.Id, s => s.DependsOn.ToHashSet());
        var sorted = new List<ISetupStep>(steps.Count);
        while (sorted.Count < steps.Count)
        {
            var ready = steps.FirstOrDefault(s =>
                !sorted.Contains(s) && remainingDeps[s.Id].Count == 0);

            if (ready is null)
            {
                var cycle = string.Join(", ", steps.Where(s => !sorted.Contains(s)).Select(s => s.Id));
                throw new InvalidOperationException($"Dependency cycle among steps: {cycle}");
            }

            sorted.Add(ready);
            foreach (var deps in remainingDeps.Values)
                deps.Remove(ready.Id);
        }

        return sorted;
    }
}
