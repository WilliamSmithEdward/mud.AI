namespace MudAI.Core.Models;

/// <summary>Classification of what happened after a command was sent.</summary>
public enum OutcomeKind
{
    Pending,
    Success,
    NoEffect,
    Error,
    Unknown
}

/// <summary>A command and how the MUD responded to it.</summary>
public sealed class CommandOutcome
{
    public required string Command { get; init; }
    public OutcomeKind Kind { get; set; } = OutcomeKind.Pending;
    public string ResponseSnippet { get; set; } = "";
    public DateTimeOffset SentAt { get; init; } = DateTimeOffset.Now;
}
