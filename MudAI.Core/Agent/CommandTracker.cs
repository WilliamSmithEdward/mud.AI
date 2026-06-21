using Microsoft.Extensions.Options;
using MudAI.Core.Configuration;
using MudAI.Core.Models;

namespace MudAI.Core.Agent;

/// <summary>
/// In-memory anti-loop tracker. Classifies each command's response, counts consecutive
/// failures per command, and suppresses a command (for a cooldown) once it fails
/// <see cref="MudAiOptions.RepeatThreshold"/> times in a row. Thread-safe.
/// </summary>
public sealed class CommandTracker(IOptions<MudAiOptions> options) : ICommandTracker
{
    // Typical MUD responses to a bad command or a move that can't happen.
    private static readonly string[] ErrorPhrases =
    [
        "huh?!?",
        "arglebargle",
        "what?",
        "i don't see",
        "i do not see",
        "you don't see",
        "you do not see",
        "you can't",
        "you cannot",
        "you don't have",
        "you do not have",
        "there is no",
        "there's no",
        "no one",
        "nobody",
        "nothing here",
        "alas, you cannot go",
        "you cannot go that way",
        "that way is blocked",
        "you are already",
        "isn't here",
        "is not here",
        "not a valid"
    ];

    private readonly MudAiOptions _options = options.Value;
    private readonly object _gate = new();

    // command -> consecutive failure count + last kind
    private readonly Dictionary<string, (int Fails, OutcomeKind LastKind)> _fails = new(StringComparer.OrdinalIgnoreCase);

    // command -> suppressed until
    private readonly Dictionary<string, DateTimeOffset> _suppressedUntil = new(StringComparer.OrdinalIgnoreCase);

    public void RecordSent(string command)
    {
        // No state change needed yet; classification happens once the response is known.
        _ = command;
    }

    public CommandOutcome ClassifyAndRecord(string command, string responseText)
    {
        var key = Normalize(command);
        var kind = Classify(responseText);

        var outcome = new CommandOutcome
        {
            Command = command,
            Kind = kind,
            ResponseSnippet = Snippet(responseText)
        };

        lock (_gate)
        {
            if (kind is OutcomeKind.Error or OutcomeKind.NoEffect)
            {
                int fails = _fails.TryGetValue(key, out var cur) ? cur.Fails + 1 : 1;
                _fails[key] = (fails, kind);

                if (fails >= _options.RepeatThreshold)
                    _suppressedUntil[key] = DateTimeOffset.Now.AddSeconds(_options.SuppressionCooldownSeconds);
            }
            else // Success / Unknown clear the failure streak
            {
                _fails.Remove(key);
                _suppressedUntil.Remove(key);
            }
        }

        return outcome;
    }

    public bool ShouldSuppress(string command)
    {
        var key = Normalize(command);
        lock (_gate)
        {
            if (!_suppressedUntil.TryGetValue(key, out var until)) return false;
            if (until > DateTimeOffset.Now) return true;
            _suppressedUntil.Remove(key); // cooldown expired
            _fails.Remove(key);
            return false;
        }
    }

    public IReadOnlyList<string> GetActiveSuppressions()
    {
        var now = DateTimeOffset.Now;
        lock (_gate)
        {
            return _suppressedUntil
                .Where(kv => kv.Value > now)
                .OrderBy(kv => kv.Value)
                .Select(kv => kv.Key)
                .ToList();
        }
    }

    public string GetFailureContext(int maxItems = 8)
    {
        lock (_gate)
        {
            var failing = _fails
                .Where(kv => kv.Value.Fails >= 2)
                .OrderByDescending(kv => kv.Value.Fails)
                .Take(maxItems)
                .Select(kv =>
                {
                    var label = kv.Value.LastKind == OutcomeKind.NoEffect ? "no effect" : "error";
                    return $"\"{kv.Key}\" ({label} x{kv.Value.Fails})";
                })
                .ToList();

            return failing.Count == 0 ? "" : string.Join(", ", failing);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _fails.Clear();
            _suppressedUntil.Clear();
        }
    }

    private static string Normalize(string command) => command.Trim().ToLowerInvariant();

    private static OutcomeKind Classify(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return OutcomeKind.NoEffect;

        var lower = response.ToLowerInvariant();
        foreach (var phrase in ErrorPhrases)
            if (lower.Contains(phrase, StringComparison.Ordinal))
                return OutcomeKind.Error;

        return OutcomeKind.Success;
    }

    private static string Snippet(string response)
    {
        var trimmed = response.Trim().ReplaceLineEndings(" ");
        return trimmed.Length <= 160 ? trimmed : trimmed[..160] + "...";
    }
}
