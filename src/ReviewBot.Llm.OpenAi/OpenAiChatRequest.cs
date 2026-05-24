namespace ReviewBot.Llm.OpenAi;

internal sealed record OpenAiChatRequest(
    string SystemPrompt,
    IReadOnlyList<string> UserMessages,
    string ModelName,
    int MaxTokens,
    float Temperature,
    string ResponseFormat,
    bool IncludeContextRequestsInJsonSchema);
