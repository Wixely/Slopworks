using Slopworks.Core.Engine;
using Slopworks.Core.State;
using Xunit;

namespace Slopworks.Core.Tests;

public class SetupStateTests
{
    [Fact]
    public void FreshJournal_IsNotComplete()
        => Assert.False(SetupState.IsComplete(new InMemoryJournal()));

    [Fact]
    public void FinalStepOk_IsComplete()
    {
        var journal = new InMemoryJournal();
        journal.Data.Steps[SetupState.FinalStepId] = new StepJournalEntry { LastState = "Ok" };

        Assert.True(SetupState.IsComplete(journal));
    }

    [Theory]
    [InlineData("Broken")]
    [InlineData("Partial")]
    [InlineData("Missing")]
    public void FinalStepNotOk_IsNotComplete(string state)
    {
        var journal = new InMemoryJournal();
        journal.Data.Steps[SetupState.FinalStepId] = new StepJournalEntry { LastState = state };

        Assert.False(SetupState.IsComplete(journal));
    }

    [Fact]
    public void EarlierStepsOkButNotFinal_IsNotComplete()
    {
        var journal = new InMemoryJournal();
        journal.Data.Steps["image.pull"] = new StepJournalEntry { LastState = "Ok" };

        Assert.False(SetupState.IsComplete(journal));
    }
}
