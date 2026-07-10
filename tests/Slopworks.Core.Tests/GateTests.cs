using Slopworks.Core.Actions;
using Xunit;

namespace Slopworks.Core.Tests;

public class InteractiveGateTests
{
    [Fact]
    public async Task Request_SurfacesPendingApproval_AndReturnsDecision()
    {
        var gate = new InteractiveGate();
        var action = EngineHarness.Action("step", "x");

        var request = gate.RequestAsync(action, CancellationToken.None);
        var pending = await gate.Pending.ReadAsync();
        pending.Resolve(ActionDecision.Approved);

        Assert.Equal(ActionDecision.Approved, await request);
        Assert.Equal("x", pending.Action.ActionId);
    }

    [Fact]
    public async Task ApprovedAllForStep_SkipsPromptsForSameStep()
    {
        var gate = new InteractiveGate();

        var first = gate.RequestAsync(EngineHarness.Action("step", "one"), CancellationToken.None);
        (await gate.Pending.ReadAsync()).Resolve(ActionDecision.ApprovedAllForStep);
        Assert.Equal(ActionDecision.ApprovedAllForStep, await first);

        // Second action for the same step must not surface a prompt.
        var second = await gate.RequestAsync(EngineHarness.Action("step", "two"), CancellationToken.None);
        Assert.Equal(ActionDecision.Approved, second);
        Assert.False(gate.Pending.TryRead(out _));
    }

    [Fact]
    public async Task ApprovedAllForStep_DoesNotLeakToOtherSteps()
    {
        var gate = new InteractiveGate();

        var first = gate.RequestAsync(EngineHarness.Action("alpha", "one"), CancellationToken.None);
        (await gate.Pending.ReadAsync()).Resolve(ActionDecision.ApprovedAllForStep);
        await first;

        var other = gate.RequestAsync(EngineHarness.Action("beta", "two"), CancellationToken.None);
        var pending = await gate.Pending.ReadAsync();
        Assert.Equal("two", pending.Action.ActionId);
        pending.Resolve(ActionDecision.Denied);
        Assert.Equal(ActionDecision.Denied, await other);
    }

    [Fact]
    public async Task Cancellation_UnblocksAwaitingRequest()
    {
        var gate = new InteractiveGate();
        using var cts = new CancellationTokenSource();

        var request = gate.RequestAsync(EngineHarness.Action("step", "x"), cts.Token);
        await gate.Pending.ReadAsync();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }
}

public class PolicyGateTests
{
    [Theory]
    [InlineData(ActionKind.WriteFile)]
    [InlineData(ActionKind.DeleteFile)]
    public async Task RootConfinedFileOps_AutoApproved(ActionKind kind)
    {
        var inner = new ScriptedGate(ActionDecision.Denied);
        var gate = new PolicyGate(inner, autoApproveInsideRoot: true);

        var decision = await gate.RequestAsync(
            EngineHarness.Action("step", "x", kind: kind, insideRoot: true), CancellationToken.None);

        Assert.Equal(ActionDecision.Approved, decision);
        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task Executions_AlwaysReachInnerGate_EvenInsideRoot()
    {
        var inner = new ScriptedGate(ActionDecision.Denied);
        var gate = new PolicyGate(inner, autoApproveInsideRoot: true);

        var decision = await gate.RequestAsync(
            EngineHarness.Action("step", "x", kind: ActionKind.Execute, insideRoot: true), CancellationToken.None);

        Assert.Equal(ActionDecision.Denied, decision);
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task FileOpsOutsideRoot_ReachInnerGate()
    {
        var inner = new ScriptedGate(ActionDecision.Denied);
        var gate = new PolicyGate(inner, autoApproveInsideRoot: true);

        var decision = await gate.RequestAsync(
            EngineHarness.Action("step", "x", kind: ActionKind.WriteFile, insideRoot: false), CancellationToken.None);

        Assert.Equal(ActionDecision.Denied, decision);
    }

    [Fact]
    public async Task PolicyDisabled_EverythingReachesInnerGate()
    {
        var inner = new ScriptedGate();
        var gate = new PolicyGate(inner, autoApproveInsideRoot: false);

        await gate.RequestAsync(
            EngineHarness.Action("step", "x", kind: ActionKind.WriteFile, insideRoot: true), CancellationToken.None);

        Assert.Single(inner.Requests);
    }
}
