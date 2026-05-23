namespace ReviewBot.Llm.Anthropic;

internal sealed record AnthropicMessageRequest(
    string SystemPrompt,
    IReadOnlyList<string> UserMessages,
    string ModelName,
    int MaxTokens,
    decimal Temperature);
