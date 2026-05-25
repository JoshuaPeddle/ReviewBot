using ReviewBot.Core.Llm;

namespace ReviewBot.Llm.OpenAi;

internal interface IOpenAiChatClient
{
    Task<OpenAiChatResult> CompleteChatAsync(OpenAiChatRequest request, CancellationToken ct);
}

internal sealed record OpenAiChatResult(string Content, LlmTokenUsage? Usage);
