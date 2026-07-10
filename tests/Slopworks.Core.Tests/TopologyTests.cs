using Slopworks.Core.Engine;
using Xunit;

namespace Slopworks.Core.Tests;

public class TopologyTests
{
    [Fact]
    public void Steps_AreSortedByDependencies()
    {
        var harness = new EngineHarness();
        var c = new FakeStep { Id = "c", DependsOn = ["b"] };
        var a = new FakeStep { Id = "a" };
        var b = new FakeStep { Id = "b", DependsOn = ["a"] };

        var engine = harness.Build(c, a, b);

        Assert.Equal(["a", "b", "c"], engine.Steps.Select(s => s.Id));
    }

    [Fact]
    public void RegistrationOrder_PreservedAmongIndependentSteps()
    {
        var harness = new EngineHarness();
        var engine = harness.Build(new FakeStep { Id = "x" }, new FakeStep { Id = "y" }, new FakeStep { Id = "z" });

        Assert.Equal(["x", "y", "z"], engine.Steps.Select(s => s.Id));
    }

    [Fact]
    public void DependencyCycle_Throws()
    {
        var harness = new EngineHarness();
        var a = new FakeStep { Id = "a", DependsOn = ["b"] };
        var b = new FakeStep { Id = "b", DependsOn = ["a"] };

        var ex = Assert.Throws<InvalidOperationException>(() => harness.Build(a, b));
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownDependency_Throws()
    {
        var harness = new EngineHarness();
        var step = new FakeStep { Id = "a", DependsOn = ["ghost"] };

        var ex = Assert.Throws<InvalidOperationException>(() => harness.Build(step));
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public void DuplicateStepId_Throws()
    {
        var harness = new EngineHarness();

        Assert.Throws<InvalidOperationException>(() =>
            harness.Build(new FakeStep { Id = "a" }, new FakeStep { Id = "a" }));
    }
}
