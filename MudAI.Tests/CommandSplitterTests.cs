using MudAI.Core.Agent;
using Xunit;

namespace MudAI.Tests;

public class CommandSplitterTests
{
    [Fact]
    public void SingleCommand_ReturnsItUntouched()
    {
        Assert.Equal(new[] { "look" }, CommandSplitter.Split("look"));
    }

    [Fact]
    public void SplitsOnSemicolons()
    {
        Assert.Equal(new[] { "open door", "north", "look" }, CommandSplitter.Split("open door;north;look"));
    }

    [Fact]
    public void TrimsPartsAndDropsEmpty()
    {
        Assert.Equal(new[] { "n", "s" }, CommandSplitter.Split(" n ; ; s ;"));
    }

    [Fact]
    public void BlankInput_ReturnsEmpty()
    {
        Assert.Empty(CommandSplitter.Split("   "));
    }
}
