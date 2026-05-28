namespace ReviewBot.Llm.Anthropic;

internal interface IAnthropicClient
{
    Task<string> CreateMessageAsync(AnthropicMessageRequest request, CancellationToken ct);

    Task<int> CountTokensAsync(AnthropicTokenCountRequest request, CancellationToken ct);
}
