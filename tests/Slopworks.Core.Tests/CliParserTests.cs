using Slopworks.Core.Cli;
using Xunit;

namespace Slopworks.Core.Tests;

public class CliParserTests
{
    [Fact]
    public void NoArgs_IsNotACommand_SoGuiRuns()
    {
        Assert.False(CliParser.IsCliCommand([]));
        Assert.Equal(CliCommand.None, CliParser.Parse([]).Command);
    }

    [Theory]
    [InlineData("start", CliCommand.Start)]
    [InlineData("stop", CliCommand.Stop)]
    [InlineData("status", CliCommand.Status)]
    [InlineData("--help", CliCommand.Help)]
    [InlineData("-h", CliCommand.Help)]
    public void RecognizesCommands_CaseInsensitive(string arg, CliCommand expected)
    {
        Assert.Equal(expected, CliParser.Parse([arg]).Command);
        Assert.Equal(expected, CliParser.Parse([arg.ToUpperInvariant()]).Command);
        Assert.True(CliParser.IsCliCommand([arg]));
    }

    [Fact]
    public void UnknownFirstArg_LeavesGuiPath()
    {
        Assert.False(CliParser.IsCliCommand(["--fullscreen"]));
        Assert.Equal(CliCommand.None, CliParser.Parse(["wat"]).Command);
    }

    [Theory]
    [InlineData("--model")]
    [InlineData("-m")]
    public void Start_ParsesModelFlag(string flag)
    {
        var invocation = CliParser.Parse(["start", flag, "org/model"]);

        Assert.Equal(CliCommand.Start, invocation.Command);
        Assert.Equal("org/model", invocation.Model);
    }

    [Fact]
    public void Start_WithoutModel_HasNullModel()
        => Assert.Null(CliParser.Parse(["start"]).Model);

    [Fact]
    public void ModelFlag_WithoutValue_IsIgnoredNotCrashing()
        => Assert.Null(CliParser.Parse(["start", "--model"]).Model);
}
