using Microsoft.Extensions.Logging;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;
using ReviewBot.Core.Prompting;

namespace ReviewBot.Llm.Anthropic;

public sealed class AnthropicReviewLlm : IConfigurableReviewLlm
{
    private const string RetryInstruction = "Your previous response was not valid JSON. Respond again with ONLY the JSON object.";
    private static readonly TimeSpan[] TransientRetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
    ];

    public string ProviderName => "anthropic";

    private readonly AnthropicLlmOptions options;
    private readonly ILogger<AnthropicReviewLlm> logger;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
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
        IAnthropicClient? client,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        this.options = options;
        this.logger = logger;
        this.delayAsync = delayAsync ?? Task.Delay;
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
            configuredClient ?? sdkClient,
            delayAsync);
    }

    private async Task<string> SendAsync(PromptPayload prompt, IReadOnlyList<string> userMessages, CancellationToken ct)
    {
        var request = new AnthropicMessageRequest(
            SystemPrompt: prompt.SystemPrompt,
            UserMessages: userMessages,
            ModelName: options.ModelName,
            MaxTokens: options.MaxTokens,
            Temperature: options.Temperature);

        for (var retryAttempt = 0; ; retryAttempt++)
        {
            try
            {
                return await GetClient().CreateMessageAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (retryAttempt < TransientRetryDelays.Length)
            {
                var delay = TransientRetryDelays[retryAttempt];
                logger.LogWarning(
                    ex,
                    "Transient Anthropic request failure; retrying after {RetryDelay}",
                    delay);
                await delayAsync(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private IAnthropicClient GetClient() =>
        configuredClient ?? (sdkClient ??= new AnthropicSdkClient(options));
}
