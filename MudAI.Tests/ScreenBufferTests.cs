using MudAI.Core.Agent;
using Xunit;

namespace MudAI.Tests;

public class ScreenBufferTests
{
    [Fact]
    public void GetRecentText_JoinsLinesAndPrompt()
    {
        var buf = new ScreenBuffer(100);
        buf.AppendLine("line one");
        buf.AppendLine("line two");
        buf.SetPrompt("HP:100 >");

        var text = buf.GetRecentText(100);
        Assert.Contains("line one", text);
        Assert.Contains("line two", text);
        Assert.Contains("HP:100 >", text);
    }

    [Fact]
    public void GetTextSince_ExcludesPrompt()
    {
        var buf = new ScreenBuffer(100);
        buf.AppendLine("old");
        buf.SetPrompt("HP:100 >");

        long mark = buf.Head;
        buf.AppendLine("server reply");

        var since = buf.GetTextSince(mark);
        Assert.Equal("server reply", since);
        Assert.DoesNotContain("HP:100", since);
    }

    [Fact]
    public void GetTextSince_NoNewLines_ReturnsEmpty()
    {
        var buf = new ScreenBuffer(100);
        buf.AppendLine("old");
        buf.SetPrompt("HP:100 >");

        long mark = buf.Head; // nothing appended after this
        Assert.Equal("", buf.GetTextSince(mark));
    }

    [Fact]
    public void Eviction_KeepsMostRecentAndTracksHead()
    {
        var buf = new ScreenBuffer(20); // minimum capacity
        for (int i = 0; i < 50; i++) buf.AppendLine($"L{i}");

        Assert.Equal(50, buf.Head);

        var recent = buf.GetRecentText(100);
        Assert.Contains("L49", recent);
        Assert.DoesNotContain("L0\n", recent + "\n"); // L0 evicted
    }

    [Fact]
    public void GetTextSince_AcrossEviction_ReturnsWhatRemains()
    {
        var buf = new ScreenBuffer(20);
        for (int i = 0; i < 50; i++) buf.AppendLine($"L{i}");

        var since = buf.GetTextSince(45); // L45..L49
        Assert.Contains("L45", since);
        Assert.Contains("L49", since);
    }

    [Fact]
    public void Clear_ResetsEverything()
    {
        var buf = new ScreenBuffer(100);
        buf.AppendLine("x");
        buf.SetPrompt("p");
        buf.Clear();

        Assert.Equal(0, buf.Head);
        Assert.Equal("", buf.CurrentPrompt);
        Assert.Equal("", buf.GetRecentText(100));
    }
}
