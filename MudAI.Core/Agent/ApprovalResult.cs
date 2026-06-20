namespace MudAI.Core.Agent;

/// <summary>The user's verdict on a proposed command (with the possibly-edited command text).</summary>
public readonly record struct ApprovalResult(bool Approved, string Command);
