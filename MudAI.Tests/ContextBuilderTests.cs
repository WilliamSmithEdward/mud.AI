using Microsoft.Extensions.Options;
using MudAI.Core.Agent;
using MudAI.Core.Configuration;
using MudAI.Core.Models;
using Xunit;

namespace MudAI.Tests;

public class ContextBuilderTests
{
    private static ContextBuilder NewBuilder(int window) =>
        new(Options.Create(new MudAiOptions
        {
            ContextWindowTokens = window,
            ReservedOutputTokens = 0,
            MaxResponseTokens = 0
        }));

    [Fact]
    public void Build_ReturnsSystemThenUser()
    {
        var builder = NewBuilder(98304);
        var messages = builder.Build(new AgentContextInput { RecentScreen = "You are here." });

        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].Role);
        Assert.Equal("user", messages[1].Role);
    }

    [Fact]
    public void Build_IncludesGoalSteeringAndGameState()
    {
        var builder = NewBuilder(98304);
        var messages = builder.Build(new AgentContextInput
        {
            RecentScreen = "screen text",
            Goal = "find the temple",
            Steering = "be cautious",
            GameStateSummary = "HP 80/120 | Room Temple"
        });

        var user = messages[1].Content;
        Assert.Contains("find the temple", user);
        Assert.Contains("be cautious", user);
        Assert.Contains("HP 80/120", user);
        Assert.Contains("screen text", user);
    }

    [Fact]
    public void Build_HugeScreen_StaysWithinBudget()
    {
        const int window = 4000;
        var builder = NewBuilder(window);
        var hugeScreen = string.Join("\n", Enumerable.Range(0, 4000).Select(i => $"line {i} with some filler text"));

        var messages = builder.Build(new AgentContextInput { RecentScreen = hugeScreen });

        int total = TokenEstimator.Estimate(messages[0].Content) + TokenEstimator.Estimate(messages[1].Content);
        Assert.True(total <= window + 64, $"total tokens {total} exceeded window {window}");
        Assert.Contains("older output truncated", messages[1].Content);
    }
}
