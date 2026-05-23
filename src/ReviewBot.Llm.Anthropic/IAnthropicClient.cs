namespace ReviewBot.Llm.Anthropic;

internal interface IAnthropicClient
{
    Task<string> CreateMessageAsync(AnthropicMessageRequest request, CancellationToken ct);
}
