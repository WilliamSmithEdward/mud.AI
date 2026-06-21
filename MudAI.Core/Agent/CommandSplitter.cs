namespace MudAI.Core.Agent;

/// <summary>
/// Splits a command line into individual commands on ';' (client-side multi-command, the way most
/// MUD clients work). Each part is trimmed; empty parts are dropped.
/// </summary>
public static class CommandSplitter
{
    public static IReadOnlyList<string> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        if (!text.Contains(';'))
        {
            var single = text.Trim();
            return single.Length == 0 ? [] : [single];
        }

        var parts = new List<string>();
        foreach (var raw in text.Split(';'))
        {
            var part = raw.Trim();
            if (part.Length > 0) parts.Add(part);
        }
        return parts;
    }
}
