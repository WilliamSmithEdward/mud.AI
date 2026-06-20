using System.Text.Json;
using System.Text.Json.Serialization;
using MudAI.Core.Models;

namespace MudAI.Core.Agent;

/// <summary>
/// Parses the model's JSON decision out of a (possibly noisy) completion. Local models
/// sometimes wrap JSON in prose or code fences, so we extract the first balanced object
/// and fall back to treating the whole text as reasoning if parsing fails.
/// </summary>
public static class DecisionParser
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static AgentDecision Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new AgentDecision { Reasoning = "(empty model response)", Wait = true };

        // Strip common markdown fences, then scan every '{' as a candidate object start.
        // A stray brace in prose (e.g. "take the {north} exit") must not shadow the real
        // decision object that follows, so we keep trying later candidates on failure.
        string text = raw.Replace("```json", " ").Replace("```", " ");

        // Cap candidate attempts so a degenerate, brace-heavy completion (untrusted model
        // output) can't drive the per-'{' scan into quadratic time. A real decision object is
        // always among the first handful of candidates.
        const int maxCandidates = 256;

        Dto? fallbackParsed = null;
        int pos = 0;
        for (int attempt = 0; attempt < maxCandidates; attempt++)
        {
            int start = text.IndexOf('{', pos);
            if (start < 0) break;

            string? json = ExtractBalancedObject(text, start);
            if (json is not null)
            {
                var dto = TryDeserialize(json);
                if (dto is not null)
                {
                    if (IsMeaningful(dto)) return Map(dto, raw);
                    fallbackParsed ??= dto; // valid JSON but no recognised fields — keep looking
                }
            }

            pos = start + 1;
        }

        return fallbackParsed is not null
            ? Map(fallbackParsed, raw)
            : new AgentDecision { Reasoning = Clamp(raw), Wait = true };
    }

    private static AgentDecision Map(Dto dto, string raw) => new()
    {
        Command = dto.Command?.Trim() ?? "",
        Reasoning = string.IsNullOrWhiteSpace(dto.Reasoning) ? Clamp(raw) : dto.Reasoning.Trim(),
        Goal = string.IsNullOrWhiteSpace(dto.Goal) ? null : dto.Goal.Trim(),
        Risk = ParseRisk(dto.Risk),
        Confidence = dto.Confidence is { } c && c is >= 0 and <= 1 ? c : 0.5,
        Wait = dto.Wait ?? false,
        Lesson = string.IsNullOrWhiteSpace(dto.Lesson) ? null : dto.Lesson.Trim()
    };

    private static bool IsMeaningful(Dto d) =>
        !string.IsNullOrWhiteSpace(d.Command)
        || !string.IsNullOrWhiteSpace(d.Reasoning)
        || !string.IsNullOrWhiteSpace(d.Goal)
        || !string.IsNullOrWhiteSpace(d.Lesson)
        || !string.IsNullOrWhiteSpace(d.Risk)
        || d.Wait.HasValue
        || d.Confidence.HasValue;

    private static Dto? TryDeserialize(string json)
    {
        try { return JsonSerializer.Deserialize<Dto>(json, Opts); }
        catch (JsonException) { return null; }
    }

    /// <summary>Returns the balanced {...} object starting at <paramref name="start"/>, ignoring
    /// braces inside JSON strings; null if it never closes.</summary>
    private static string? ExtractBalancedObject(string text, int start)
    {
        int depth = 0;
        bool inString = false;
        bool escape = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return text.Substring(start, i - start + 1);
                    break;
            }
        }

        return null; // unbalanced
    }

    private static RiskLevel ParseRisk(string? risk) => risk?.Trim().ToLowerInvariant() switch
    {
        "high" => RiskLevel.High,
        "medium" or "med" => RiskLevel.Medium,
        _ => RiskLevel.Low
    };

    private static string Clamp(string s)
    {
        s = s.Trim();
        return s.Length <= 400 ? s : s[..400] + "…";
    }

    private sealed class Dto
    {
        [JsonPropertyName("reasoning")] public string? Reasoning { get; set; }
        [JsonPropertyName("command")] public string? Command { get; set; }
        [JsonPropertyName("goal")] public string? Goal { get; set; }
        [JsonPropertyName("risk")] public string? Risk { get; set; }
        [JsonPropertyName("confidence")] public double? Confidence { get; set; }
        [JsonPropertyName("wait")] public bool? Wait { get; set; }
        [JsonPropertyName("lesson")] public string? Lesson { get; set; }
    }
}
