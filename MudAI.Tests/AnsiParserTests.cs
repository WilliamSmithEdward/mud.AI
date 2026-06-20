using MudAI.Core.Ansi;
using MudAI.Core.Models;
using Xunit;

namespace MudAI.Tests;

public class AnsiParserTests
{
    private readonly AnsiParser _parser = new();

    [Fact]
    public void PlainText_ProducesSingleDefaultSegment()
    {
        var (segments, _) = _parser.ParseLine("hello world", SgrState.Default);

        var seg = Assert.Single(segments);
        Assert.Equal("hello world", seg.Text);
        Assert.Equal(AnsiColor.Default, seg.Foreground);
        Assert.False(seg.Bold);
    }

    [Fact]
    public void Sgr_SetsForegroundColor()
    {
        var (segments, _) = _parser.ParseLine("\x1b[31mred", SgrState.Default);

        var seg = Assert.Single(segments);
        Assert.Equal("red", seg.Text);
        Assert.Equal(AnsiColor.Red, seg.Foreground);
    }

    [Fact]
    public void Sgr_BoldGreen_SetsBoldAndColor()
    {
        var (segments, _) = _parser.ParseLine("\x1b[1;32mx", SgrState.Default);

        var seg = Assert.Single(segments);
        Assert.True(seg.Bold);
        Assert.Equal(AnsiColor.Green, seg.Foreground);
    }

    [Fact]
    public void Sgr_ResetReturnsToDefault()
    {
        var (segments, _) = _parser.ParseLine("\x1b[31mA\x1b[0mB", SgrState.Default);

        Assert.Equal(2, segments.Count);
        Assert.Equal(AnsiColor.Red, segments[0].Foreground);
        Assert.Equal(AnsiColor.Default, segments[1].Foreground);
        Assert.False(segments[1].Bold);
    }

    [Fact]
    public void State_CarriesAcrossLines()
    {
        var (_, state) = _parser.ParseLine("\x1b[31mstart", SgrState.Default);
        var (segments, _) = _parser.ParseLine("continued", state);

        Assert.Equal(AnsiColor.Red, Assert.Single(segments).Foreground);
    }

    [Fact]
    public void Extended256Color_ZeroIndex_DoesNotResetOtherAttributes()
    {
        // Regression: "ESC[38;5;0m" must NOT be read as SGR 0 (full reset).
        var (_, boldState) = _parser.ParseLine("\x1b[1m", SgrState.Default);
        var (segments, _) = _parser.ParseLine("\x1b[38;5;0mX", boldState);

        var seg = Assert.Single(segments);
        Assert.True(seg.Bold); // bold preserved, not wiped by the 0 sub-parameter
        Assert.Equal(AnsiColor.Black, seg.Foreground);
    }

    [Fact]
    public void TrueColor_RedTriple_MapsToRed_AndConsumesSubParams()
    {
        var (segments, _) = _parser.ParseLine("\x1b[38;2;255;0;0mX", SgrState.Default);

        var seg = Assert.Single(segments);
        Assert.Equal(AnsiColor.Red, seg.Foreground);
        Assert.Equal("X", seg.Text); // sub-params consumed, not rendered as text
    }

    [Fact]
    public void TrueColor_TrailingCodeAfterColor_StillApplied()
    {
        var (segments, _) = _parser.ParseLine("\x1b[38;2;10;10;10;4mX", SgrState.Default);

        var seg = Assert.Single(segments);
        Assert.True(seg.Underline); // the trailing 4 (underline) is not skipped
    }

    [Fact]
    public void Strip_RemovesSgr()
    {
        Assert.Equal("hello", _parser.Strip("\x1b[31mhello\x1b[0m"));
    }

    [Fact]
    public void Strip_PreservesNewlineInMalformedCsi()
    {
        // Regression: a CSI aborted by a newline must not swallow the newline + following text.
        Assert.Equal("\nhello", _parser.Strip("\x1b[1\nhello"));
    }

    [Fact]
    public void EmptySgr_IsReset()
    {
        var (_, state) = _parser.ParseLine("\x1b[1;31m", SgrState.Default);
        var (_, reset) = _parser.ParseLine("\x1b[m", state);
        Assert.Equal(SgrState.Default, reset);
    }
}
