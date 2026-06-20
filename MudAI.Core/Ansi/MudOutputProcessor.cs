using System.Text;
using MudAI.Core.Models;

namespace MudAI.Core.Ansi;

/// <summary>
/// Turns the raw telnet text stream into per-line <see cref="MudMessage"/>s plus a live
/// "prompt" (the trailing text that has no newline yet — MUD prompts like <c>&lt; 100hp &gt;</c>).
/// SGR colour state carries across lines. Not thread-safe: feed it from a single reader thread.
/// </summary>
public sealed class MudOutputProcessor(IAnsiParser parser)
{
    private readonly StringBuilder _partial = new();
    private SgrState _state = SgrState.Default;

    /// <summary>A newline-terminated line is ready.</summary>
    public event EventHandler<MudMessage>? LineCompleted;

    /// <summary>The trailing partial line (prompt) changed. Not appended to scrollback.</summary>
    public event EventHandler<MudMessage>? PromptChanged;

    public void Reset()
    {
        _partial.Clear();
        _state = SgrState.Default;
    }

    public void Ingest(string chunk)
    {
        foreach (char c in chunk)
        {
            if (c == '\n')
            {
                EmitLine(_partial.ToString());
                _partial.Clear();
            }
            else
            {
                _partial.Append(c);
            }
        }

        // Whatever remains without a newline is the current prompt. Parse it with the
        // current state but DO NOT advance _state (the full line is re-parsed on newline).
        if (PromptChanged is not null)
        {
            string prompt = _partial.ToString();
            var (segments, _) = parser.ParseLine(prompt, _state);
            PromptChanged.Invoke(this, new MudMessage
            {
                Direction = MessageDirection.Incoming,
                PlainText = Concat(segments),
                Segments = segments
            });
        }
    }

    private void EmitLine(string rawLine)
    {
        var (segments, newState) = parser.ParseLine(rawLine, _state);
        _state = newState;
        LineCompleted?.Invoke(this, new MudMessage
        {
            Direction = MessageDirection.Incoming,
            PlainText = Concat(segments),
            Segments = segments
        });
    }

    private static string Concat(IReadOnlyList<AnsiSegment> segments)
    {
        if (segments.Count == 0) return "";
        if (segments.Count == 1) return segments[0].Text;
        var sb = new StringBuilder();
        foreach (var s in segments) sb.Append(s.Text);
        return sb.ToString();
    }
}
