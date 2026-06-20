using System.Text;

namespace MudAI.Core.Agent;

/// <summary>
/// Thread-safe rolling buffer of recent MUD output lines plus the live prompt. Each line
/// has a global index so the orchestrator can grab exactly "what arrived since I sent X".
/// </summary>
public sealed class ScreenBuffer
{
    private readonly int _capacity;
    private readonly List<string> _lines = [];
    private readonly object _gate = new();
    private long _baseIndex; // global index of _lines[0]
    private string _prompt = "";

    public ScreenBuffer(int capacity) => _capacity = Math.Max(20, capacity);

    /// <summary>Global index of the next line to be appended.</summary>
    public long Head
    {
        get { lock (_gate) return _baseIndex + _lines.Count; }
    }

    public string CurrentPrompt
    {
        get { lock (_gate) return _prompt; }
    }

    public void AppendLine(string plain)
    {
        lock (_gate)
        {
            _lines.Add(plain);
            while (_lines.Count > _capacity)
            {
                _lines.RemoveAt(0);
                _baseIndex++;
            }
        }
    }

    public void SetPrompt(string prompt)
    {
        lock (_gate) _prompt = prompt;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            _baseIndex = 0;
            _prompt = "";
        }
    }

    /// <summary>The most recent <paramref name="maxLines"/> lines plus the current prompt.</summary>
    public string GetRecentText(int maxLines)
    {
        lock (_gate)
        {
            int take = Math.Min(maxLines, _lines.Count);
            int start = _lines.Count - take;
            var sb = new StringBuilder();
            for (int i = start; i < _lines.Count; i++)
                sb.Append(_lines[i]).Append('\n');
            if (_prompt.Length > 0) sb.Append(_prompt);
            return sb.ToString().TrimEnd('\n');
        }
    }

    /// <summary>
    /// Text of all lines whose global index is &gt;= <paramref name="marker"/>. The live prompt is
    /// deliberately EXCLUDED so a redrawn prompt is not mistaken for a command's reply — an empty
    /// result means no new lines arrived. If lines were evicted, returns what remains.
    /// </summary>
    public string GetTextSince(long marker)
    {
        lock (_gate)
        {
            int start = (int)Math.Clamp(marker - _baseIndex, 0, _lines.Count);
            var sb = new StringBuilder();
            for (int i = start; i < _lines.Count; i++)
                sb.Append(_lines[i]).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }
    }
}
