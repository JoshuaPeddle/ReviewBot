namespace ReviewBot.Llm.OpenAi;

internal interface IOpenAiChatClient
{
    Task<string> CompleteChatAsync(OpenAiChatRequest request, CancellationToken ct);
}
