using Microsoft.Extensions.Logging;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;
using ReviewBot.Core.Prompting;

namespace ReviewBot.Llm.Anthropic;

public sealed class AnthropicReviewLlm : IConfigurableReviewLlm
{
    private const string RetryInstruction = "Your previous response was not valid JSON. Respond again with ONLY the JSON object.";
    public string ProviderName => "anthropic";

    private readonly AnthropicLlmOptions options;
    private readonly ILogger<AnthropicReviewLlm> logger;
    private readonly IAnthropicClient? configuredClient;
    private IAnthropicClient? sdkClient;

    public AnthropicReviewLlm(
        AnthropicLlmOptions options,
        ILogger<AnthropicReviewLlm> logger,
        HttpClient? httpClient = null)
        : this(options, logger, httpClient is null ? null : new AnthropicSdkClient(options, httpClient))
    {
    }

    internal AnthropicReviewLlm(
        AnthropicLlmOptions options,
        ILogger<AnthropicReviewLlm> logger,
        IAnthropicClient? client)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        this.options = options;
        this.logger = logger;
        configuredClient = client;
    }

    public async Task<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prompt = PromptBuilder.Build(request);
        var firstResponse = await SendAsync(prompt, [prompt.UserPrompt], ct);
        var firstParse = LlmResultParser.Parse(firstResponse, logger);
        if (firstParse is { Success: true, Value: not null })
        {
            return firstParse.Value;
        }

        logger.LogWarning("Anthropic response was not valid review JSON; retrying once. Error: {Error}", firstParse.Error);

        var retryResponse = await SendAsync(prompt, [prompt.UserPrompt, RetryInstruction], ct);
        var retryParse = LlmResultParser.Parse(retryResponse, logger);
        if (retryParse is { Success: true, Value: not null })
        {
            return retryParse.Value;
        }

        throw new LlmResponseException(retryResponse, retryParse.Error);
    }

    public IReviewLlm WithModelName(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        return new AnthropicReviewLlm(
            options with { ModelName = modelName },
            logger,
            configuredClient ?? sdkClient);
    }

    private Task<string> SendAsync(PromptPayload prompt, IReadOnlyList<string> userMessages, CancellationToken ct) =>
        GetClient().CreateMessageAsync(
            new AnthropicMessageRequest(
                SystemPrompt: prompt.SystemPrompt,
                UserMessages: userMessages,
                ModelName: options.ModelName,
                MaxTokens: options.MaxTokens,
                Temperature: options.Temperature),
            ct);

    private IAnthropicClient GetClient() =>
        configuredClient ?? (sdkClient ??= new AnthropicSdkClient(options));
}
