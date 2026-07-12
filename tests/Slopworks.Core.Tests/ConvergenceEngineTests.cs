using Slopworks.Core.Actions;
using Slopworks.Core.Engine;
using Xunit;

namespace Slopworks.Core.Tests;

public class ConvergenceEngineTests
{
    [Fact]
    public async Task AllStepsOk_ConvergesWithoutPlanning()
    {
        var harness = new EngineHarness();
        var step = new FakeStep { Id = "a", Detections = [StepDetection.Ok("already good")] };
        var engine = harness.Build(step);

        var result = await engine.ConvergeAsync(harness.Progress, CancellationToken.None);

        Assert.Equal(RunStatus.Converged, result.Status);
        Assert.Equal(0, step.PlanCalls);
        Assert.Equal("Ok", harness.Journal.Data.Steps["a"].LastState);
    }

    [Fact]
    public async Task MissingStep_AppliesActionsAndVerifies()
    {
        var harness = new EngineHarness();
        var executed = 0;
        var step = new FakeStep
        {
            Id = "a",
            Detections = [StepDetection.Missing("not installed"), StepDetection.Ok("installed")],
        };
        step.Plan.Add(EngineHarness.Action("a", "a.install", (_, _) =>
        {
            executed++;
            return Task.FromResult(ActionResult.Success());
        }));

        var result = await harness.Build(step).ConvergeAsync(harness.Progress, CancellationToken.None);

        Assert.Equal(RunStatus.Converged, result.Status);
        Assert.Equal(1, executed);
        Assert.Equal("Ok", harness.Journal.Data.Steps["a"].LastState);
        Assert.Contains(harness.Events, e => e is EngineEvent.ActionCompleted { Result.Succeeded: true });
    }

    [Fact]
    public async Task ActionFailure_HaltsRun_LaterStepsNeverStart()
    {
        var harness = new EngineHarness();
        var first = new FakeStep { Id = "a", Detections = [StepDetection.Missing("missing")] };
        first.Plan.Add(EngineHarness.Action("a", "a.fail", (_, _) => Task.FromResult(ActionResult.Failure("boom"))));
        var second = new FakeStep { Id = "b", DependsOn = ["a"] };

        var result = await harness.Build(first, second).ConvergeAsync(harness.Progress, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal("a", result.FailedStepId);
        Assert.Equal(0, second.DetectCalls);
    }

    [Fact]
    public async Task DeniedAction_FailsStepWithoutExecuting()
    {
        var harness = new EngineHarness { Gate = new ScriptedGate(ActionDecision.Denied) };
        var executed = 0;
        var step = new FakeStep { Id = "a", Detections = [StepDetection.Missing("missing")] };
        step.Plan.Add(EngineHarness.Action("a", "a.x", (_, _) =>
        {
            executed++;
            return Task.FromResult(ActionResult.Success());
        }));

        var result = await harness.Build(step).ConvergeAsync(harness.Progress, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(0, executed);
        Assert.Contains(harness.Events, e => e is EngineEvent.StepCompleted { Outcome: StepOutcome.Denied });
    }

    [Fact]
    public async Task AbortedAction_StopsEntireRun()
    {
        var harness = new EngineHarness { Gate = new ScriptedGate(ActionDecision.Aborted) };
        var step = new FakeStep { Id = "a", Detections = [StepDetection.Missing("missing")] };
        step.Plan.Add(EngineHarness.Action("a", "a.x"));
        var second = new FakeStep { Id = "b" };

        var result = await harness.Build(step, second).ConvergeAsync(harness.Progress, CancellationToken.None);

        Assert.Equal(RunStatus.Aborted, result.Status);
        Assert.Equal(0, second.DetectCalls);
    }

    [Fact]
    public async Task RebootRequired_JournalsResumePointAndEndsRun()
    {
        var harness = new EngineHarness();
        var step = new FakeStep { Id = "wsl.feature", Detections = [StepDetection.Missing("missing")] };
        step.Plan.Add(EngineHarness.Action("wsl.feature", "install", (_, _) =>
            Task.FromResult(ActionResult.NeedsReboot("WSL feature enabled"))));
        var second = new FakeStep { Id = "later", DependsOn = ["wsl.feature"] };

        var result = await harness.Build(step, second).ConvergeAsync(harness.Progress, CancellationToken.None);

        Assert.Equal(RunStatus.RebootRequired, result.Status);
        Assert.Equal("wsl.feature", harness.Journal.Data.PendingReboot!.AfterStep);
        Assert.Equal(0, second.DetectCalls);
        Assert.Contains(harness.Events, e => e is EngineEvent.RebootRequired);
    }

    [Fact]
    public async Task NotApplicableStep_IsSkippedWithoutDetection()
    {
        var harness = new EngineHarness();
        var gpuStep = new FakeStep { Id = "gpu", Applies = false };

        var result = await harness.Build(gpuStep).ConvergeAsync(harness.Progress, CancellationToken.None);

        Assert.Equal(RunStatus.Converged, result.Status);
        Assert.Equal(0, gpuStep.DetectCalls);
        Assert.Contains(harness.Events, e => e is EngineEvent.StepCompleted { Outcome: StepOutcome.Skipped });
    }

    [Fact]
    public async Task NonOkStepWithEmptyPlan_FailsLoudly()
    {
        var harness = new EngineHarness();
        var step = new FakeStep { Id = "a", Detections = [StepDetection.Broken("broken, no plan")] };

        var result = await harness.Build(step).ConvergeAsync(harness.Progress, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("planned no corrective actions", result.Detail);
    }

    [Fact]
    public async Task VerificationStillBroken_FailsStep()
    {
        var harness = new EngineHarness();
        var step = new FakeStep
        {
            Id = "a",
            Detections = [StepDetection.Missing("missing"), StepDetection.Broken("still broken")],
        };
        step.Plan.Add(EngineHarness.Action("a", "a.x"));

        var result = await harness.Build(step).ConvergeAsync(harness.Progress, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("still broken", result.Detail);
    }

    [Fact]
    public async Task CancellationDuringAction_ReturnsCancelled()
    {
        var harness = new EngineHarness();
        using var cts = new CancellationTokenSource();
        var step = new FakeStep { Id = "a", Detections = [StepDetection.Missing("missing")] };
        step.Plan.Add(EngineHarness.Action("a", "a.slow", async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return ActionResult.Success();
        }));

        var result = await harness.Build(step).ConvergeAsync(harness.Progress, cts.Token);

        Assert.Equal(RunStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task ActionThrowing_BecomesFailureNotCrash()
    {
        var harness = new EngineHarness();
        var step = new FakeStep { Id = "a", Detections = [StepDetection.Missing("missing")] };
        step.Plan.Add(EngineHarness.Action("a", "a.throws", (_, _) => throw new InvalidOperationException("kaput")));

        var result = await harness.Build(step).ConvergeAsync(harness.Progress, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("kaput", result.Detail);
    }

    [Fact]
    public async Task ConvergeToTarget_RunsOnlyDependencyChain()
    {
        var harness = new EngineHarness();
        var a = new FakeStep { Id = "a" };
        var b = new FakeStep { Id = "b", DependsOn = ["a"] };
        var unrelated = new FakeStep { Id = "c" };

        var result = await harness.Build(a, b, unrelated)
            .ConvergeAsync(harness.Progress, CancellationToken.None, targetStepId: "b");

        Assert.Equal(RunStatus.Converged, result.Status);
        Assert.Equal(1, a.DetectCalls);
        Assert.Equal(1, b.DetectCalls);
        Assert.Equal(0, unrelated.DetectCalls);
    }

    [Fact]
    public async Task ConvergeSingle_FailsWhenDependencyIsNotOk()
    {
        var harness = new EngineHarness();
        var dep = new FakeStep { Id = "dep", Detections = [StepDetection.Missing("dep missing")] };
        var target = new FakeStep { Id = "target", DependsOn = ["dep"] };

        var result = await harness.Build(dep, target)
            .ConvergeSingleAsync("target", harness.Progress, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("dep", result.Detail);
        Assert.Equal(0, target.DetectCalls);
    }

    [Fact]
    public async Task ConvergeSingle_RunsWhenDependenciesOk()
    {
        var harness = new EngineHarness();
        var dep = new FakeStep { Id = "dep" };
        var target = new FakeStep
        {
            Id = "target",
            DependsOn = ["dep"],
            Detections = [StepDetection.Partial("half done"), StepDetection.Ok("repaired")],
        };
        target.Plan.Add(EngineHarness.Action("target", "repair"));

        var result = await harness.Build(dep, target)
            .ConvergeSingleAsync("target", harness.Progress, CancellationToken.None);

        Assert.Equal(RunStatus.Converged, result.Status);
        Assert.Equal("Ok", harness.Journal.Data.Steps["target"].LastState);
    }

    [Fact]
    public async Task ChoiceAction_ExecutesTheChosenAlternative()
    {
        var gate = new ScriptedGate(ActionDecision.Approved) { ChoiceIndex = 1 };
        var harness = new EngineHarness { Gate = gate };
        var executed = new List<string>();
        var step = new FakeStep { Id = "a", Detections = [StepDetection.Missing("missing"), StepDetection.Ok("ok")] };
        step.Plan.Add(EngineHarness.Action("a", "pick") with
        {
            Choices =
            [
                new ActionChoice("default", "d", (_, _) => { executed.Add("default"); return Task.FromResult(ActionResult.Success()); }),
                new ActionChoice("alternative", "a", (_, _) => { executed.Add("alternative"); return Task.FromResult(ActionResult.Success()); }),
            ],
        });

        var result = await harness.Build(step).ConvergeAsync(harness.Progress, CancellationToken.None);

        Assert.Equal(RunStatus.Converged, result.Status);
        Assert.Equal(["alternative"], executed);
    }

    [Fact]
    public async Task ChoiceAction_AutoGateTakesTheDefault()
    {
        var harness = new EngineHarness(); // AutoApproveGate
        var executed = new List<string>();
        var step = new FakeStep { Id = "a", Detections = [StepDetection.Missing("missing"), StepDetection.Ok("ok")] };
        step.Plan.Add(EngineHarness.Action("a", "pick") with
        {
            Choices =
            [
                new ActionChoice("default", "d", (_, _) => { executed.Add("default"); return Task.FromResult(ActionResult.Success()); }),
                new ActionChoice("alternative", "a", (_, _) => { executed.Add("alternative"); return Task.FromResult(ActionResult.Success()); }),
            ],
        });

        await harness.Build(step).ConvergeAsync(harness.Progress, CancellationToken.None);

        Assert.Equal(["default"], executed);
    }

    [Fact]
    public async Task DetectAll_SurvivesThrowingStep()
    {
        var harness = new EngineHarness();
        var throwing = new ThrowingDetectStep();
        var fine = new FakeStep { Id = "fine" };

        var results = await harness.Build(throwing, fine).DetectAllAsync(harness.Progress, CancellationToken.None);

        Assert.Equal(StepState.Unknown, results["explodes"].State);
        Assert.Equal(StepState.Ok, results["fine"].State);
    }

    private sealed class ThrowingDetectStep : ISetupStep
    {
        public string Id => "explodes";
        public string Title => "explodes";
        public IReadOnlyList<string> DependsOn => [];
        public bool AppliesTo(SystemProfile profile) => true;

        public Task<StepDetection> DetectAsync(StepContext ctx, CancellationToken ct)
            => throw new InvalidOperationException("probe exploded");

        public Task<IReadOnlyList<PlannedAction>> PlanAsync(StepContext ctx, StepDetection detection, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<PlannedAction>>([]);
    }
}
