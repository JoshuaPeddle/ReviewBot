namespace ReviewBot.Llm.OpenAi;

internal sealed record OpenAiChatRequest(
    string SystemPrompt,
    IReadOnlyList<string> UserMessages,
    int MaxTokens,
    float Temperature,
    bool UseJsonMode);
