using MudAI.Core.Agent;
using MudAI.Core.Models;
using Xunit;

namespace MudAI.Tests;

public class DecisionParserTests
{
    [Fact]
    public void ParsesPlainJson()
    {
        var d = DecisionParser.Parse("""{"command":"north","reasoning":"go north","risk":"low","confidence":0.8}""");

        Assert.Equal("north", d.Command);
        Assert.Equal("go north", d.Reasoning);
        Assert.Equal(RiskLevel.Low, d.Risk);
        Assert.Equal(0.8, d.Confidence, 3);
        Assert.False(d.Wait);
    }

    [Fact]
    public void IgnoresProseBraceBeforeRealObject()
    {
        var d = DecisionParser.Parse("""Sure, I'll take the {north} exit. {"command":"north"}""");
        Assert.Equal("north", d.Command);
    }

    [Fact]
    public void StripsCodeFences()
    {
        var d = DecisionParser.Parse("```json\n{\"command\":\"look\"}\n```");
        Assert.Equal("look", d.Command);
    }

    [Fact]
    public void NoJson_FallsBackToWait()
    {
        var d = DecisionParser.Parse("I am thinking about what to do next.");
        Assert.True(d.Wait);
        Assert.False(d.HasCommand);
        Assert.Contains("thinking", d.Reasoning);
    }

    [Fact]
    public void RespectsWaitFlag()
    {
        var d = DecisionParser.Parse("""{"wait":true,"reasoning":"observe"}""");
        Assert.True(d.Wait);
    }

    [Fact]
    public void SkipsEmptyObjectForLaterMeaningfulOne()
    {
        var d = DecisionParser.Parse("""{} then {"command":"south"}""");
        Assert.Equal("south", d.Command);
    }

    [Fact]
    public void HandlesBracesInsideStrings()
    {
        var d = DecisionParser.Parse("""{"command":"say {hi}","reasoning":"greet"}""");
        Assert.Equal("say {hi}", d.Command);
    }

    [Fact]
    public void EmptyInput_Waits()
    {
        var d = DecisionParser.Parse("");
        Assert.True(d.Wait);
    }
}
