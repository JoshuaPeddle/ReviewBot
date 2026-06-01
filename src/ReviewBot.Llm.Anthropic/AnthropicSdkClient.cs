using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using ReviewBot.Core.Llm;

namespace ReviewBot.Llm.Anthropic;

internal sealed class AnthropicSdkClient : IAnthropicClient
{
    private readonly AnthropicClient client;

    public AnthropicSdkClient(AnthropicLlmOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new ArgumentException("Anthropic API key must be configured.", nameof(options));
        }

        client = new AnthropicClient(
            new APIAuthentication(options.ApiKey),
            httpClient,
            requestInterceptor: null);
    }

    public async Task<AnthropicMessageResult> CreateMessageAsync(AnthropicMessageRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = BuildParameters(request);

        var response = await client.Messages.GetClaudeMessageAsync(parameters, ct);
        var textParts = response.Content?
            .OfType<TextContent>()
            .Select(content => content.Text)
            .Where(text => !string.IsNullOrEmpty(text))
            .ToArray();

        var content = textParts is { Length: > 0 }
            ? string.Concat(textParts)
            : response.Message.ToString();

        var usage = response.Usage is not null
            ? new LlmTokenUsage(
                PromptTokens: response.Usage.InputTokens,
                CompletionTokens: response.Usage.OutputTokens,
                CachedPromptTokens: response.Usage.CacheReadInputTokens)
            : null;

        return new AnthropicMessageResult(content, usage);
    }

    public async Task<int> CountTokensAsync(AnthropicTokenCountRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = await client.Messages.CountMessageTokensAsync(BuildTokenCountParameters(request), ct);
        return response.InputTokens;
    }

    internal static MessageParameters BuildParameters(AnthropicMessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cacheControl = request.EnablePromptCaching
            ? new CacheControl { Type = CacheControlType.ephemeral }
            : null;

        var parameters = new MessageParameters
        {
            MaxTokens = request.MaxTokens,
            Model = request.ModelName,
            Stream = false,
            Temperature = request.Temperature,
            PromptCaching = request.EnablePromptCaching ? PromptCacheType.FineGrained : PromptCacheType.None,
            System =
            [
                new SystemMessage(request.SystemPrompt, cacheControl)
            ],
            Messages = request.UserMessages
                .Select(userMessage => new Message(RoleType.User, userMessage, cacheControl: null))
                .ToList()
        };

        return parameters;
    }

    internal static MessageCountTokenParameters BuildTokenCountParameters(AnthropicTokenCountRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new MessageCountTokenParameters
        {
            Model = request.ModelName,
            System = string.IsNullOrEmpty(request.SystemPrompt)
                ? []
                : [new SystemMessage(request.SystemPrompt)],
            Messages = request.UserMessages
                .Select(userMessage => new Message(RoleType.User, userMessage))
                .ToList()
        };
    }
}
