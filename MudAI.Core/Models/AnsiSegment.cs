namespace MudAI.Core.Models;

/// <summary>The 8 base ANSI colors plus "default" (terminal foreground/background).</summary>
public enum AnsiColor
{
    Default = -1,
    Black = 0,
    Red = 1,
    Green = 2,
    Yellow = 3,
    Blue = 4,
    Magenta = 5,
    Cyan = 6,
    White = 7
}

/// <summary>
/// A run of text sharing a single ANSI style. A rendered line is a sequence of these.
/// <see cref="Bold"/> doubles as the "bright" attribute: renderers should use the bright
/// palette for a bold colored segment.
/// </summary>
public sealed record AnsiSegment(
    string Text,
    AnsiColor Foreground,
    AnsiColor Background,
    bool Bold,
    bool Underline,
    bool Inverse);
