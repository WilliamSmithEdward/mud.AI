using System.Text;
using MudAI.Core.Models;

namespace MudAI.Core.Ansi;

/// <summary>
/// Parses CSI SGR sequences (<c>ESC [ ... m</c>) into styled segments and ignores other
/// CSI/escape sequences (cursor moves, OSC, etc.). Control characters are dropped; tabs
/// become spaces. A single line never contains a newline (the caller splits lines first).
/// </summary>
public sealed class AnsiParser : IAnsiParser
{
    private const char Esc = '\x1b';

    public (IReadOnlyList<AnsiSegment> Segments, SgrState NewState) ParseLine(string line, SgrState state)
    {
        var segments = new List<AnsiSegment>();
        var sb = new StringBuilder(line.Length);
        int i = 0;

        void Flush()
        {
            if (sb.Length == 0) return;
            segments.Add(new AnsiSegment(sb.ToString(),
                state.Foreground, state.Background, state.Bold, state.Underline, state.Inverse));
            sb.Clear();
        }

        while (i < line.Length)
        {
            char c = line[i];

            if (c == Esc && i + 1 < line.Length && line[i + 1] == '[')
            {
                int j = i + 2;
                while (j < line.Length && IsCsiParam(line[j])) j++;
                if (j >= line.Length) break; // incomplete CSI at end of line: drop remainder

                char final = line[j];
                if (IsCsiFinal(final))
                {
                    if (final == 'm')
                    {
                        Flush();
                        state = ApplySgr(state, line.AsSpan(i + 2, j - (i + 2)));
                    }
                    // other CSI finals (H, J, K, ...) are display control we don't render: skip
                    i = j + 1;
                }
                else
                {
                    // A non-final byte (e.g. a control char) aborts the sequence; resume at it.
                    i = j;
                }
            }
            else if (c == Esc)
            {
                // Lone ESC or a non-CSI escape (e.g. ESC ] OSC ...): skip ESC and the next byte.
                i += 2;
            }
            else if (c == '\r')
            {
                i++;
            }
            else if (c == '\t')
            {
                sb.Append("    ");
                i++;
            }
            else if (c < 0x20)
            {
                i++; // drop other control chars
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        Flush();
        return (segments, state);
    }

    private static SgrState ApplySgr(SgrState s, ReadOnlySpan<char> paramText)
    {
        // Empty parameter (ESC[m) == reset.
        if (paramText.IsEmpty)
            return SgrState.Default;

        var ranges = SplitSemicolons(paramText);
        for (int idx = 0; idx < ranges.Count; idx++)
        {
            var part = paramText[ranges[idx]];
            int code = part.IsEmpty ? 0 : (int.TryParse(part, out var v) ? v : -999);

            // Extended colour: 38/48 ; 5 ; n  (256-colour)  or  38/48 ; 2 ; r ; g ; b  (truecolour).
            // The sub-parameters MUST be consumed, not re-fed to the switch (else a 0 channel
            // would trigger a full reset and channel values would be read as colour/attr codes).
            if (code is 38 or 48)
            {
                bool fg = code == 38;
                int mode = idx + 1 < ranges.Count && int.TryParse(paramText[ranges[idx + 1]], out var m) ? m : -1;
                if (mode == 5)
                {
                    int n = idx + 2 < ranges.Count && int.TryParse(paramText[ranges[idx + 2]], out var ci) ? ci : -1;
                    s = ApplyExtended(s, fg, MapXterm256(n));
                    idx += 2;
                }
                else if (mode == 2)
                {
                    int r = idx + 2 < ranges.Count && int.TryParse(paramText[ranges[idx + 2]], out var rv) ? rv : 0;
                    int g = idx + 3 < ranges.Count && int.TryParse(paramText[ranges[idx + 3]], out var gv) ? gv : 0;
                    int b = idx + 4 < ranges.Count && int.TryParse(paramText[ranges[idx + 4]], out var bv) ? bv : 0;
                    s = ApplyExtended(s, fg, MapRgb(r, g, b));
                    idx += 4;
                }
                else
                {
                    idx += 1; // unknown extended mode: skip the mode token
                }
                continue;
            }

            s = code switch
            {
                0 => SgrState.Default,
                1 => s with { Bold = true },
                4 => s with { Underline = true },
                7 => s with { Inverse = true },
                22 => s with { Bold = false },
                24 => s with { Underline = false },
                27 => s with { Inverse = false },
                39 => s with { Foreground = AnsiColor.Default },
                49 => s with { Background = AnsiColor.Default },
                >= 30 and <= 37 => s with { Foreground = (AnsiColor)(code - 30) },
                >= 40 and <= 47 => s with { Background = (AnsiColor)(code - 40) },
                >= 90 and <= 97 => s with { Foreground = (AnsiColor)(code - 90), Bold = true },
                >= 100 and <= 107 => s with { Background = (AnsiColor)(code - 100) },
                _ => s
            };
        }

        return s;
    }

    private static SgrState ApplyExtended(SgrState s, bool foreground, (AnsiColor Color, bool Bright) c) =>
        foreground
            ? s with { Foreground = c.Color, Bold = s.Bold || c.Bright }
            : s with { Background = c.Color };

    /// <summary>Maps an xterm-256 index onto the nearest of our 8 base colours (+ bright flag).</summary>
    private static (AnsiColor Color, bool Bright) MapXterm256(int idx)
    {
        if (idx < 0) return (AnsiColor.Default, false);
        if (idx < 8) return ((AnsiColor)idx, false);
        if (idx < 16) return ((AnsiColor)(idx - 8), true);
        if (idx < 232)
        {
            int n = idx - 16;
            return MapRgb((n / 36) * 51, ((n / 6) % 6) * 51, (n % 6) * 51);
        }
        if (idx < 256)
        {
            int level = (idx - 232) * 10 + 8;
            return MapRgb(level, level, level);
        }
        return (AnsiColor.Default, false);
    }

    /// <summary>Maps an RGB triple onto the nearest of our 8 base colours (+ bright flag).</summary>
    private static (AnsiColor Color, bool Bright) MapRgb(int r, int g, int b)
    {
        const int threshold = 96;
        const int brightAt = 180;
        int bits = (r >= threshold ? 1 : 0) | (g >= threshold ? 2 : 0) | (b >= threshold ? 4 : 0);
        bool bright = Math.Max(r, Math.Max(g, b)) >= brightAt;
        return ((AnsiColor)bits, bright);
    }

    private static bool IsCsiParam(char c) => c is >= '\x20' and <= '\x3f';   // params + intermediates
    private static bool IsCsiFinal(char c) => c is >= '\x40' and <= '\x7e';   // CSI final byte range

    private static List<Range> SplitSemicolons(ReadOnlySpan<char> text)
    {
        var ranges = new List<Range>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == ';')
            {
                ranges.Add(new Range(start, i));
                start = i + 1;
            }
        }
        ranges.Add(new Range(start, text.Length));
        return ranges;
    }

    public string Strip(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == Esc)
            {
                if (i + 1 < text.Length && text[i + 1] == '[')
                {
                    int j = i + 2;
                    while (j < text.Length && IsCsiParam(text[j])) j++;
                    // Consume through a real final byte; otherwise the sequence is aborted
                    // (e.g. a newline) and we resume AT that byte so it is preserved.
                    i = (j < text.Length && IsCsiFinal(text[j])) ? j : j - 1;
                }
                else
                {
                    i++; // skip the byte after a lone ESC
                }
            }
            else if (c == '\t') sb.Append("    ");
            else if (c == '\r') { /* drop */ }
            else if (c >= 0x20 || c == '\n') sb.Append(c);
        }
        return sb.ToString();
    }
}
