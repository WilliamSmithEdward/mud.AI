namespace MudAI.Core.Models;

/// <summary>
/// One line of traffic for display: ANSI-stripped <see cref="PlainText"/> (what the LLM sees)
/// plus styled <see cref="Segments"/> (what the UI renders).
/// </summary>
public sealed class MudMessage
{
    public required MessageDirection Direction { get; init; }

    /// <summary>ANSI/control-stripped text. Used for the agent's screen buffer and prompts.</summary>
    public required string PlainText { get; init; }

    /// <summary>Styled runs for coloured rendering. Empty for outgoing/system lines.</summary>
    public IReadOnlyList<AnsiSegment> Segments { get; init; } = [];

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public static MudMessage System(string text) =>
        new() { Direction = MessageDirection.System, PlainText = text };

    public static MudMessage Outgoing(string text) =>
        new() { Direction = MessageDirection.Outgoing, PlainText = text };
}
