namespace ReviewBot.Core.Context;

/// <summary>
/// One named slice of the content budget that a prompt section consumed.
/// Tokens is the estimated cost that was charged, not a limit.
/// </summary>
public sealed record PromptBudgetSection(string Name, int Tokens);
