using Microsoft.Extensions.Options;
using MudAI.Core.Agent;
using MudAI.Core.Configuration;
using MudAI.Core.Models;
using Xunit;

namespace MudAI.Tests;

public class CommandTrackerTests
{
    private static CommandTracker NewTracker(int threshold = 3, int cooldown = 120) =>
        new(Options.Create(new MudAiOptions
        {
            RepeatThreshold = threshold,
            SuppressionCooldownSeconds = cooldown
        }));

    [Fact]
    public void ClassifiesSuccess()
    {
        var t = NewTracker();
        var outcome = t.ClassifyAndRecord("look", "You are standing in a room.");
        Assert.Equal(OutcomeKind.Success, outcome.Kind);
    }

    [Fact]
    public void ClassifiesError()
    {
        var t = NewTracker();
        var outcome = t.ClassifyAndRecord("north", "Alas, you cannot go that way.");
        Assert.Equal(OutcomeKind.Error, outcome.Kind);
    }

    [Fact]
    public void ClassifiesNoEffectOnEmptyResponse()
    {
        var t = NewTracker();
        var outcome = t.ClassifyAndRecord("wiggle", "");
        Assert.Equal(OutcomeKind.NoEffect, outcome.Kind);
    }

    [Fact]
    public void SuppressesAfterThresholdConsecutiveFailures()
    {
        var t = NewTracker(threshold: 3);
        t.ClassifyAndRecord("north", "Alas, you cannot go that way.");
        Assert.False(t.ShouldSuppress("north"));
        t.ClassifyAndRecord("north", "Alas, you cannot go that way.");
        Assert.False(t.ShouldSuppress("north"));
        t.ClassifyAndRecord("north", "Alas, you cannot go that way.");
        Assert.True(t.ShouldSuppress("north"));
    }

    [Fact]
    public void SuccessClearsFailureStreak()
    {
        var t = NewTracker(threshold: 3);
        t.ClassifyAndRecord("north", "Alas, you cannot go that way.");
        t.ClassifyAndRecord("north", "Alas, you cannot go that way.");
        t.ClassifyAndRecord("north", "You walk north into a forest."); // success
        t.ClassifyAndRecord("north", "Alas, you cannot go that way.");
        Assert.False(t.ShouldSuppress("north")); // streak reset, only 1 failure since
    }

    [Fact]
    public void FailureContext_ListsRepeatedFailures()
    {
        var t = NewTracker();
        t.ClassifyAndRecord("open door", "There is no door here.");
        t.ClassifyAndRecord("open door", "There is no door here.");
        Assert.Contains("open door", t.GetFailureContext());
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var t = NewTracker(threshold: 1);
        t.ClassifyAndRecord("foo", "Huh?!?");
        Assert.True(t.ShouldSuppress("foo"));
        t.Reset();
        Assert.False(t.ShouldSuppress("foo"));
    }

    [Fact]
    public void SuppressionIsCaseInsensitive()
    {
        var t = NewTracker(threshold: 1);
        t.ClassifyAndRecord("North", "Alas, you cannot go that way.");
        Assert.True(t.ShouldSuppress("north"));
    }
}
