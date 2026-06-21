using System.Globalization;
using System.Text;
using MudAI.Core.Models;

namespace MudAI.Core.Memory;

/// <summary>
/// Writes a human-readable snapshot of the awareness knowledge base (and the command track record)
/// to a markdown file. The file is fully regenerated each time from the DB, so it can never drift.
/// Written atomically (temp file + replace) so a crash never leaves a half-written file.
/// </summary>
public static class AwarenessExporter
{
    public static async Task ExportAsync(
        string path,
        IReadOnlyList<AwarenessEntry> entries,
        IReadOnlyList<CommandKnowledge> commands,
        int roomsMapped)
    {
        var sb = new StringBuilder();

        var byCategory = entries
            .GroupBy(e => e.Category)
            .OrderBy(g => CategoryOrder(g.Key))
            .ToList();

        string stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        sb.Append("# MudAI - Learned Awareness\n");
        sb.Append($"_Updated {stamp} | {entries.Count} facts across {byCategory.Count} categories | {roomsMapped} rooms mapped_\n\n");

        if (entries.Count == 0)
            sb.Append("_No awareness facts recorded yet._\n\n");

        foreach (var group in byCategory)
        {
            sb.Append($"## {Title(group.Key)} ({group.Count()})\n");
            foreach (var e in group.OrderByDescending(x => x.Confidence).ThenByDescending(x => x.TimesReinforced))
                sb.Append($"- [{e.Confidence:0.00} x{e.TimesReinforced}] {e.Subject}: {e.Fact}\n");
            sb.Append('\n');
        }

        if (commands.Count > 0)
        {
            sb.Append("## Command track record (from gameplay)\n");
            foreach (var c in commands)
                sb.Append($"- {c.Command}: {c.SuccessCount} ok / {c.FailureCount} failed\n");
            sb.Append('\n');
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Unique temp name so overlapping exports can never collide on one fixed path; the
        // File.Move onto the final path is itself atomic (last complete writer wins).
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temp, sb.ToString());
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort cleanup */ }
            throw;
        }
    }

    private static int CategoryOrder(string category)
    {
        int i = 0;
        foreach (var c in AwarenessVocabulary.Categories)
        {
            if (string.Equals(c, category, StringComparison.OrdinalIgnoreCase)) return i;
            i++;
        }
        return int.MaxValue;
    }

    private static string Title(string category) =>
        category.Length == 0 ? category : char.ToUpperInvariant(category[0]) + category[1..];
}
