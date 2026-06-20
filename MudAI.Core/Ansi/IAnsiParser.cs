using MudAI.Core.Models;

namespace MudAI.Core.Ansi;

/// <summary>Immutable SGR (Select Graphic Rendition) state carried between lines.</summary>
public readonly record struct SgrState(
    AnsiColor Foreground,
    AnsiColor Background,
    bool Bold,
    bool Underline,
    bool Inverse)
{
    public static SgrState Default { get; } =
        new(AnsiColor.Default, AnsiColor.Default, false, false, false);
}

/// <summary>
/// Stateless ANSI parser. Each call takes the incoming SGR state and returns the styled
/// segments plus the resulting state, so callers can carry colour across lines without
/// the parser holding mutable state (keeping it thread-safe / singleton-friendly).
/// </summary>
public interface IAnsiParser
{
    (IReadOnlyList<AnsiSegment> Segments, SgrState NewState) ParseLine(string line, SgrState state);

    /// <summary>Strips ANSI escape codes and control characters, leaving plain text.</summary>
    string Strip(string text);
}
