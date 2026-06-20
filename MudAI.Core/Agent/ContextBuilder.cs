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
        called Arctic, a DikuMUD-style game, through a telnet client. You read the recent
        game screen and decide the single best next command to type.

        How to play well:
        - Issue exactly ONE concrete MUD command per turn (e.g. "look", "north", "score",
          "kill rat", "get sword", "wear armor"). Use real MUD verbs.
        - When you first connect you may be at a login/menu/character screen. Read it
          carefully and respond appropriately (enter a name, choose menu options, press
          enter, etc.). Lines you type are sent verbatim.
        - Prefer observing ("look", reading prompts) when unsure rather than guessing wildly.
        - NEVER repeat a command that has been failing. If something didn't work, try a
          genuinely different approach. The "commands that are failing" list is authoritative.
        - Build a mental map: note exits, rooms, and dangers. Record durable insights as a
          "lesson" so you remember them next session.
        - Stay alive: flee or heal when in danger; don't attack things far above your level.

        Respond with ONLY a single JSON object, no prose, no markdown fences:
        {
          "reasoning": "<one or two sentences on why>",
          "command": "<the exact text to send, or empty string to wait>",
          "goal": "<optional updated short-term goal>",
          "risk": "low|medium|high",
          "confidence": 0.0-1.0,
          "wait": false,
          "lesson": "<optional durable insight worth remembering, else empty>"
        }
        Set "wait": true (and command "") only if the right move is to observe and let more
        output arrive. Keep "reasoning" short. Output the JSON object and nothing else.
        """;

    public IReadOnlyList<ChatMessage> Build(AgentContextInput input)
    {
        var system = ChatMessage.System(SystemPrompt);

        // --- fixed (non-screen) user sections ---
        var header = new StringBuilder();

        header.Append("CURRENT GOAL: ")
              .AppendLine(string.IsNullOrWhiteSpace(input.Goal) ? "(none yet — decide one)" : input.Goal);

        if (!string.IsNullOrWhiteSpace(input.Steering))
        {
            header.AppendLine().AppendLine("LIVE STEERING FROM THE HUMAN (follow this):")
                  .AppendLine(input.Steering.Trim());
        }

        if (!string.IsNullOrWhiteSpace(input.GameStateSummary))
        {
            header.AppendLine().Append("LIVE GAME STATE (authoritative, from the MUD): ")
                  .AppendLine(input.GameStateSummary);
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

        var memory = BuildMemorySection(input.Lessons, input.Commands);
        if (memory.Length > 0)
        {
            header.AppendLine().AppendLine("WHAT YOU'VE LEARNED SO FAR:").Append(memory);
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
