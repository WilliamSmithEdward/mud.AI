namespace MudAI.Core.Agent;

/// <summary>
/// Cheap heuristic token estimate (~4 characters per token). Good enough for budgeting
/// against a local model's context window without pulling in a real tokenizer.
/// </summary>
public static class TokenEstimator
{
    public static int Estimate(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    public static int Estimate(IEnumerable<string> texts) =>
        texts.Sum(Estimate);
}
