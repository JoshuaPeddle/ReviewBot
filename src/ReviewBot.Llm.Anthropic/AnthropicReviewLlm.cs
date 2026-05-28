using Microsoft.Extensions.Logging;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;
using ReviewBot.Core.Prompting;

namespace ReviewBot.Llm.Anthropic;

public sealed class AnthropicReviewLlm : IConfigurableReviewLlm
{
    private const int MaxLoggedRawResponseLength = 500;
    private static readonly TimeSpan[] TransientRetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
    ];

    public string ProviderName => "anthropic";

    public bool SupportsParallelRequests => true;

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
        var (firstResponse, firstUsage) = await SendAsync(prompt, [prompt.UserPrompt], enablePromptCaching: true, "review", ct);
        var firstParse = LlmResultParser.Parse(firstResponse, logger);
        if (firstParse is { Success: true, Value: not null })
        {
            return firstParse.Value with { TokenUsage = firstUsage };
        }

        logger.LogWarning(
            "Anthropic response was not valid review JSON; attempting repair. Error: {Error}; RawResponse: {RawResponse}",
            firstParse.Error,
            Truncate(firstResponse, MaxLoggedRawResponseLength));
        ct.ThrowIfCancellationRequested();

        var repairPrompt = BuildRepairPrompt(firstResponse, request.Config.Review.AgenticContext);
        var (repairResponse, repairUsage) = await SendAsync(repairPrompt, [repairPrompt.UserPrompt], enablePromptCaching: false, "review", ct);
        var totalUsage = firstUsage?.Add(repairUsage) ?? repairUsage;
        var repairParse = LlmResultParser.Parse(repairResponse, logger);
        if (repairParse is { Success: true, Value: not null })
        {
            ReviewBotLlmMetrics.RecordParseFailure(ProviderName, repaired: true);
            return repairParse.Value with { TokenUsage = totalUsage };
        }

        logger.LogWarning(
            "Anthropic response repair failed; returning empty review result. Error: {Error}; RawResponse: {RawResponse}",
            repairParse.Error,
            Truncate(repairResponse, MaxLoggedRawResponseLength));
        ReviewBotLlmMetrics.RecordParseFailure(ProviderName, repaired: false);
        return new ReviewResult(string.Empty, []) { TokenUsage = totalUsage };
    }

    public async Task<string> CompleteRawAsync(PromptPayload prompt, CancellationToken ct, string phase = "review")
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);

        var (content, _) = await SendAsync(prompt, [prompt.UserPrompt], enablePromptCaching: true, phase, ct);
        return content;
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

    private async Task<(string Content, LlmTokenUsage? Usage)> SendAsync(
        PromptPayload prompt,
        IReadOnlyList<string> userMessages,
        bool enablePromptCaching,
        string phase,
        CancellationToken ct)
    {
        var request = new AnthropicMessageRequest(
            SystemPrompt: prompt.SystemPrompt,
            UserMessages: userMessages,
            ModelName: options.ModelName,
            MaxTokens: options.MaxTokens,
            Temperature: options.Temperature,
            EnablePromptCaching: enablePromptCaching && options.PromptCachingEnabled);

        for (var retryAttempt = 0; ; retryAttempt++)
        {
            try
            {
                var result = await GetClient().CreateMessageAsync(request, ct).ConfigureAwait(false);
                if (result.Usage is not null)
                {
                    ReviewBotLlmMetrics.RecordTokenUsage(ProviderName, phase, result.Usage);
                    if (result.Usage.CachedPromptTokens > 0)
                    {
                        logger.LogDebug(
                            "Anthropic response reported {CachedTokens} cached prompt tokens for phase {Phase}",
                            result.Usage.CachedPromptTokens,
                            phase);
                    }
                }

                return (result.Content, result.Usage);
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

    private static PromptPayload BuildRepairPrompt(string failedResponse, bool includeContextRequests)
    {
        var schema = ReviewJsonSchema.Build(includeContextRequests);
        return new PromptPayload(
            $"Your previous response was not valid JSON. Return only a JSON object matching this schema: {schema}",
            failedResponse);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
