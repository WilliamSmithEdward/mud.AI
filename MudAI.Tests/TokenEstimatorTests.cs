using MudAI.Core.Agent;
using Xunit;

namespace MudAI.Tests;

public class TokenEstimatorTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("abcd", 1)]
    [InlineData("abcdefgh", 2)]
    [InlineData("a", 1)]
    public void Estimate_ApproximatesFourCharsPerToken(string text, int expected)
    {
        Assert.Equal(expected, TokenEstimator.Estimate(text));
    }

    [Fact]
    public void Estimate_Null_IsZero()
    {
        Assert.Equal(0, TokenEstimator.Estimate((string?)null));
    }

    [Fact]
    public void Estimate_Enumerable_Sums()
    {
        Assert.Equal(2, TokenEstimator.Estimate(["abcd", "abcd"]));
    }
}
