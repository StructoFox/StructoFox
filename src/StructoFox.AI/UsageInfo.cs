namespace StructoFox.AI;

/// <summary>
/// Token usage reported by an AI provider after a completed response.
/// Used for the context-window usage bar and, in future, AIMEM.SYS budget accounting.
/// </summary>
/// <param name="InputTokens">Tokens consumed by the prompt (history + system + user message).</param>
/// <param name="OutputTokens">Tokens generated in the response.</param>
public sealed record UsageInfo(int InputTokens, int OutputTokens);
