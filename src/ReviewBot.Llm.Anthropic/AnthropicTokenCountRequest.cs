namespace ReviewBot.Llm.Anthropic;

internal sealed record AnthropicTokenCountRequest(
    string ModelName,
    string? SystemPrompt,
    IReadOnlyList<string> UserMessages);
