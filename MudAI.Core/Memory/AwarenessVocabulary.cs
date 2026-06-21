namespace MudAI.Core.Memory;

/// <summary>
/// The closed category taxonomy the model files awareness facts under, plus normalization and
/// length clamps. Kept in one place so the store, the parser, the prompt, and tests agree.
/// </summary>
public static class AwarenessVocabulary
{
    /// <summary>Categories the model may use (lowercase). Unknown/empty input normalizes to "misc".</summary>
    public static readonly IReadOnlyList<string> Categories =
        ["geography", "navigation", "combat", "skills", "progression", "economy", "npcs", "misc"];

    private static readonly HashSet<string> Set = new(Categories, StringComparer.OrdinalIgnoreCase);

    public const int MaxSubjectLength = 60;
    public const int MaxFactLength = 200;

    /// <summary>Snaps any input to a valid category; unknown or empty becomes "misc".</summary>
    public static string Normalize(string? category)
    {
        var c = category?.Trim().ToLowerInvariant();
        return !string.IsNullOrEmpty(c) && Set.Contains(c) ? c : "misc";
    }

    public static string ClampSubject(string? s) => Clamp((s ?? "").Trim(), MaxSubjectLength);
    public static string ClampFact(string? s) => Clamp((s ?? "").Trim(), MaxFactLength);

    private static string Clamp(string s, int max) => s.Length <= max ? s : s[..max];
}
