using ReviewBot.Core.Llm;

namespace ReviewBot.Llm.Anthropic;

internal sealed record AnthropicMessageResult(string Content, LlmTokenUsage? Usage);

internal interface IAnthropicClient
{
    Task<AnthropicMessageResult> CreateMessageAsync(AnthropicMessageRequest request, CancellationToken ct);

    Task<int> CountTokensAsync(AnthropicTokenCountRequest request, CancellationToken ct);
}
