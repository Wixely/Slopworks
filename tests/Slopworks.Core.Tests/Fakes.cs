using Microsoft.Extensions.Logging.Abstractions;
using Slopworks.Core.Actions;
using Slopworks.Core.Config;
using Slopworks.Core.Engine;
using Slopworks.Core.Logging;
using Slopworks.Core.State;
using Slopworks.Platform.Abstractions;

namespace Slopworks.Core.Tests;

internal sealed class FakeStep : ISetupStep
{
    public string Id { get; init; } = "step";
    public string Title => Id;
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public bool Applies { get; init; } = true;

    /// <summary>Consumed one per Detect/Verify call; the last entry repeats.</summary>
    public List<StepDetection> Detections { get; init; } = [StepDetection.Ok("ok")];

    public List<PlannedAction> Plan { get; init; } = [];

    public int DetectCalls { get; private set; }
    public int PlanCalls { get; private set; }

    public bool AppliesTo(SystemProfile profile) => Applies;

    public Task<StepDetection> DetectAsync(StepContext ctx, CancellationToken ct)
    {
        var detection = Detections[Math.Min(DetectCalls, Detections.Count - 1)];
        DetectCalls++;
        return Task.FromResult(detection);
    }

    public Task<IReadOnlyList<PlannedAction>> PlanAsync(StepContext ctx, StepDetection detection, CancellationToken ct)
    {
        PlanCalls++;
        return Task.FromResult<IReadOnlyList<PlannedAction>>(Plan);
    }
}

internal sealed class InMemoryJournal : IStateJournal
{
    public JournalData Data { get; } = new();
    public int SaveCount { get; private set; }

    public Task SaveAsync(CancellationToken ct = default)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}

internal sealed class ScriptedGate(params ActionDecision[] script) : IActionGate
{
    private int _next;

    public List<PlannedAction> Requests { get; } = [];

    /// <summary>Choice index returned with every approval (default 0).</summary>
    public int ChoiceIndex { get; set; }

    public Task<GateResult> RequestAsync(PlannedAction action, CancellationToken ct)
    {
        Requests.Add(action);
        var decision = _next < script.Length ? script[_next++] : ActionDecision.Approved;
        return Task.FromResult(new GateResult(decision, ChoiceIndex));
    }
}

internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<ProcessSpec> Invocations { get; } = [];
    public ProcessResult Result { get; set; } = new(0, "", "", TimeSpan.Zero);

    public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? liveOutput, CancellationToken ct)
    {
        Invocations.Add(spec);
        return Task.FromResult(Result);
    }
}

internal sealed class EngineHarness
{
    public InMemoryJournal Journal { get; } = new();
    public List<EngineEvent> Events { get; } = [];
    public FakeProcessRunner ProcessRunner { get; } = new();
    public IActionGate Gate { get; set; } = new AutoApproveGate(NullLogger.Instance);

    public IProgress<EngineEvent> Progress => new InlineProgress<EngineEvent>(Events.Add);

    public ConvergenceEngine Build(params ISetupStep[] steps)
    {
        var paths = new SlopworksPaths(Path.Combine(Path.GetTempPath(), "slopworks-tests"));
        var context = new StepContext
        {
            Paths = paths,
            Config = new SlopworksConfig(),
            Profile = SystemProfile.Unknown,
            Logger = NullLogger.Instance,
            Journal = Journal,
            Probes = ProcessRunner,
        };

        return new ConvergenceEngine(steps, new EngineServices
        {
            StepContext = context,
            Gate = Gate,
            ProcessRunner = ProcessRunner,
            CommandLog = NullCommandLog.Instance,
            Logger = NullLogger.Instance,
        });
    }

    public static PlannedAction Action(
        string stepId,
        string actionId,
        Func<ActionExecutionContext, CancellationToken, Task<ActionResult>>? execute = null,
        ActionKind kind = ActionKind.Execute,
        bool insideRoot = false)
        => new(actionId, stepId, kind, $"action {actionId}", $"detail {actionId}", insideRoot,
            execute ?? ((_, _) => Task.FromResult(ActionResult.Success())));
}
