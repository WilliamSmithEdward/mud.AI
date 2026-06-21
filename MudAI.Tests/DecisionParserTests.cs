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

    [Fact]
    public void ParsesAwarenessObject()
    {
        var d = DecisionParser.Parse("""{"command":"look","awareness":{"category":"combat","subject":"rat","fact":"easy"}}""");
        Assert.NotNull(d.Awareness);
        Assert.Equal("combat", d.Awareness!.Category);
        Assert.Equal("rat", d.Awareness.Subject);
        Assert.Equal("easy", d.Awareness.Fact);
    }

    [Fact]
    public void ParsesAwarenessBareString_AsMisc()
    {
        var d = DecisionParser.Parse("""{"command":"look","awareness":"rats are easy to kill"}""");
        Assert.NotNull(d.Awareness);
        Assert.Equal("misc", d.Awareness!.Category);
        Assert.Contains("rats", d.Awareness.Fact);
    }

    [Fact]
    public void AwarenessOnly_IsMeaningful()
    {
        var d = DecisionParser.Parse("""{"awareness":{"category":"geography","subject":"midgaard","fact":"central hub"}}""");
        Assert.NotNull(d.Awareness);
        Assert.Equal("midgaard", d.Awareness!.Subject);
    }

    [Fact]
    public void MissingAwareness_IsNull()
    {
        var d = DecisionParser.Parse("""{"command":"north"}""");
        Assert.Null(d.Awareness);
    }

    [Fact]
    public void ContainsBalancedObject_DetectsCompletionForEarlyStop()
    {
        Assert.True(DecisionParser.ContainsBalancedObject("""{"command":"north"}"""));
        Assert.False(DecisionParser.ContainsBalancedObject("""{"command":"nor"""));   // still streaming
        Assert.True(DecisionParser.ContainsBalancedObject("""before {"a":"}{"} after""")); // braces in string ignored
        Assert.False(DecisionParser.ContainsBalancedObject("no json yet"));
    }
}
