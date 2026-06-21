using System.Text;
using Microsoft.Extensions.Options;
using MudAI.Core.Configuration;
using MudAI.Core.Llm;
using MudAI.Core.Models;

namespace MudAI.Core.Agent;

/// <summary>
/// Builds the system + user messages for a turn. The fixed sections (role, memory, goal,
/// steering, failures) are assembled first; whatever token budget remains is filled with
/// the tail of the recent screen so we never exceed the model's context window.
/// </summary>
public sealed class ContextBuilder(IOptions<MudAiOptions> options) : IContextBuilder
{
    private readonly MudAiOptions _options = options.Value;

    private const string SystemPrompt = """
        You are an autonomous agent learning to play a text-based MUD (Multi-User Dungeon)
        through a telnet client. You read the recent game screen and decide the single best
        next command to type.

        How to play well:
        - Issue a concrete MUD command (e.g. "look", "north", "score", "kill rat", "get sword").
          Use real MUD verbs. You may chain a short, safe sequence in one turn by separating
          commands with semicolons, e.g. "open door;north;look". Prefer a single command when you
          need to see the result before deciding the next step.
        - To travel quickly, chain movement directions with semicolons, e.g. "w;w;n;w;s" walks
          west, west, north, west, south in one turn. Chain moves only along a path you are
          confident about; send a single move when exploring an unknown exit.
        - When you first connect you may be at a login/menu/character screen. Read it
          carefully and respond appropriately (enter a name, choose menu options, press
          enter, etc.). Lines you type are sent verbatim.
        - Prefer observing ("look", reading prompts) when unsure rather than guessing wildly.
        - NEVER repeat a command that has been failing. If something didn't work, try a
          genuinely different approach. The "commands that are failing" list is authoritative.
        - Build a mental map: note exits, rooms, and dangers. Record a one-off tactical insight as a
          "lesson". For durable, organized knowledge, file an "awareness" note: pick a category from
          [geography, navigation, combat, skills, progression, economy, npcs, misc], a short subject
          (a name/place/topic), and a concise fact. Re-file the same subject to refine what you know.
          File at most one awareness note per turn, and only when you genuinely learned something durable.
        - Stay alive: flee or heal when in danger; don't attack things far above your level.

        Respond with ONLY a single JSON object, no prose, no markdown fences:
        {
          "reasoning": "<one or two sentences on why>",
          "command": "<the exact text to send, or empty string to wait>",
          "goal": "<optional updated short-term goal>",
          "risk": "low|medium|high",
          "confidence": 0.0-1.0,
          "wait": false,
          "lesson": "<optional durable insight worth remembering, else empty>",
          "awareness": { "category": "<one from the list, or omit>", "subject": "<short key>", "fact": "<concise fact>" }
        }
        Use "wait": true (with command "") sparingly, only while output is actively scrolling and
        you must let it finish. If the screen is static and nothing new is happening, do NOT wait:
        take a proactive action toward your goal (move to an exit, look, check inventory or score).
        Keep "reasoning" short. Output the JSON object and nothing else.
        """;

    public IReadOnlyList<ChatMessage> Build(AgentContextInput input)
    {
        var system = ChatMessage.System(SystemPrompt);

        // --- fixed (non-screen) user sections ---
        var header = new StringBuilder();

        // Sections are ordered stable-first so the model server's KV cache can reuse the longest
        // unchanged prefix between turns: slow-changing knowledge (goal, awareness, memory, map)
        // comes first; the every-turn state (steering, idle note, live state) and the screen go
        // last, where they change anyway.
        header.Append("CURRENT GOAL: ")
              .AppendLine(string.IsNullOrWhiteSpace(input.Goal) ? "(none yet, decide one)" : input.Goal);

        string awareness = BuildAwarenessSection(input.Awareness);
        if (awareness.Length > 0)
        {
            header.AppendLine().AppendLine("WHAT YOU KNOW (organized memory across sessions):").Append(awareness);
        }

        var memory = BuildMemorySection(input.Lessons, input.Commands);
        if (memory.Length > 0)
        {
            header.AppendLine().AppendLine("WHAT YOU'VE LEARNED SO FAR:").Append(memory);
        }

        if (!string.IsNullOrWhiteSpace(input.MapRecall))
        {
            header.AppendLine().Append("KNOWN MAP (from past exploration): ")
                  .AppendLine(input.MapRecall);
        }

        if (!string.IsNullOrWhiteSpace(input.FailureContext))
        {
            header.AppendLine().Append("COMMANDS THAT ARE FAILING (do NOT repeat these): ")
                  .AppendLine(input.FailureContext);
        }

        if (input.Suppressed.Count > 0)
        {
            header.AppendLine().Append("SUPPRESSED (forbidden this turn): ")
                  .AppendLine(string.Join(", ", input.Suppressed.Select(s => $"\"{s}\"")));
        }

        if (!string.IsNullOrWhiteSpace(input.Steering))
        {
            header.AppendLine().AppendLine("LIVE STEERING FROM THE HUMAN (follow this):")
                  .AppendLine(input.Steering.Trim());
        }

        if (input.NoNewOutput)
        {
            header.AppendLine().AppendLine(
                "NOTE: No new output from the MUD since your last action, it is idle. Do not wait; take a proactive step toward your goal now.");
        }

        if (!string.IsNullOrWhiteSpace(input.GameStateSummary))
        {
            header.AppendLine().Append("LIVE GAME STATE (authoritative, from the MUD): ")
                  .AppendLine(input.GameStateSummary);
        }

        header.AppendLine().AppendLine("RECENT GAME SCREEN (most recent at the bottom):");

        // --- budget the screen into whatever space is left ---
        int budget = _options.ContextWindowTokens - _options.ReservedOutputTokens - _options.MaxResponseTokens;
        int fixedTokens = TokenEstimator.Estimate(SystemPrompt)
                          + TokenEstimator.Estimate(header.ToString())
                          + 64; // misc framing overhead
        // Never let the floor push the total past the budget: if the fixed sections already
        // fill the window, the screen gets no room rather than a forced minimum.
        int screenTokenBudget = Math.Max(0, budget - fixedTokens);

        string screen = screenTokenBudget == 0 ? "" : TrimToTokenTail(input.RecentScreen, screenTokenBudget);

        var user = ChatMessage.User(header.Append(screen).ToString());
        return [system, user];
    }

    /// <summary>
    /// Renders awareness entries grouped by category, one line per category (subjects joined with
    /// " | "), trimmed to the configured token cap as a hard backstop against prompt bloat.
    /// </summary>
    private string BuildAwarenessSection(IReadOnlyList<AwarenessEntry> awareness)
    {
        if (awareness.Count == 0) return "";

        // Order categories by their strongest entry so that when the token cap binds it drops the
        // least-valuable categories, not whichever happen to sort last alphabetically.
        var lines = awareness
            .GroupBy(a => a.Category)
            .Select(g => new
            {
                Rank = g.Max(a => a.Confidence),
                Line = $"[{g.Key}] {string.Join(" | ", g.Select(a => $"{a.Subject}: {a.Fact}"))}"
            })
            .OrderByDescending(g => g.Rank)
            .Select(g => g.Line);

        var sb = new StringBuilder();
        int used = 0;
        foreach (var line in lines)
        {
            int cost = TokenEstimator.Estimate(line) + 1;
            if (used + cost > _options.AwarenessRecallTokens) break; // hard backstop
            sb.AppendLine(line);
            used += cost;
        }
        return sb.ToString();
    }

    private static string BuildMemorySection(
        IReadOnlyList<Lesson> lessons, IReadOnlyList<CommandKnowledge> commands)
    {
        if (lessons.Count == 0 && commands.Count == 0) return "";

        var sb = new StringBuilder();
        foreach (var lesson in lessons)
            sb.Append("- ").AppendLine(lesson.Text);

        if (commands.Count > 0)
        {
            sb.AppendLine("Command track record:");
            foreach (var c in commands)
                sb.Append("  ").Append(c.Command)
                  .Append(": ").Append(c.SuccessCount).Append(" ok / ")
                  .Append(c.FailureCount).AppendLine(" failed");
        }

        return sb.ToString();
    }

    /// <summary>Keep the last ~<paramref name="tokenBudget"/> tokens of text (most recent output).</summary>
    private static string TrimToTokenTail(string text, int tokenBudget)
    {
        int maxChars = tokenBudget * 4;
        if (text.Length <= maxChars) return text;

        int cut = text.Length - maxChars;
        // Snap to the next line boundary so we don't cut mid-line.
        int nl = text.IndexOf('\n', cut);
        if (nl >= 0 && nl < text.Length - 1) cut = nl + 1;

        return "...[older output truncated]...\n" + text[cut..];
    }
}
