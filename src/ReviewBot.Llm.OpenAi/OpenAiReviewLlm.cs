using Microsoft.Extensions.Logging;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;
using ReviewBot.Core.Prompting;

namespace ReviewBot.Llm.OpenAi;

public sealed class OpenAiReviewLlm : IConfigurableReviewLlm, IModelContextProbe
{
    private const int MaxLoggedRawResponseLength = 500;
    private static readonly TimeSpan[] TransientRetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
    ];

    public string ProviderName => "openai";

    public string ModelName => options.ModelName;

    public bool SupportsParallelRequests => false;

    private readonly OpenAiLlmOptions options;
    private readonly ILogger<OpenAiReviewLlm> logger;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly IOpenAiChatClient? configuredClient;
    private IOpenAiChatClient? sdkClient;

    public OpenAiReviewLlm(OpenAiLlmOptions options, ILogger<OpenAiReviewLlm> logger)
        : this(options, logger, null)
    {
    }

    internal OpenAiReviewLlm(
        OpenAiLlmOptions options,
        ILogger<OpenAiReviewLlm> logger,
        IOpenAiChatClient? client,
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
        var responseFormat = OpenAiResponseFormats.Normalize(options.ResponseFormat);
        var includeContextRequests = request.Config.Review.AgenticContext;
        var (firstResponse, firstUsage) = await SendAsync(prompt, [prompt.UserPrompt], responseFormat, includeContextRequests, "review", ct);
        var firstParse = LlmResultParser.Parse(firstResponse, logger);
        if (firstParse is { Success: true, Value: not null })
        {
            return firstParse.Value with { TokenUsage = firstUsage, RawLlmResponse = firstResponse };
        }

        logger.LogWarning(
            "OpenAI-compatible response was not valid review JSON; attempting repair. Error: {Error}; RawResponse: {RawResponse}",
            firstParse.Error,
            Truncate(firstResponse, MaxLoggedRawResponseLength));
        ct.ThrowIfCancellationRequested();

        var repairPrompt = BuildRepairPrompt(firstResponse, includeContextRequests);
        var (repairResponse, repairUsage) = await SendAsync(repairPrompt, [repairPrompt.UserPrompt], responseFormat, includeContextRequests, "review", ct);
        var totalUsage = firstUsage?.Add(repairUsage) ?? repairUsage;
        var repairParse = LlmResultParser.Parse(repairResponse, logger);
        if (repairParse is { Success: true, Value: not null })
        {
            ReviewBotLlmMetrics.RecordParseFailure(ProviderName, repaired: true);
            return repairParse.Value with { TokenUsage = totalUsage, RawLlmResponse = firstResponse };
        }

        logger.LogWarning(
            "OpenAI-compatible response repair failed; returning empty review result. Error: {Error}; RawResponse: {RawResponse}",
            repairParse.Error,
            Truncate(repairResponse, MaxLoggedRawResponseLength));
        ReviewBotLlmMetrics.RecordParseFailure(ProviderName, repaired: false);
        return new ReviewResult(string.Empty, []) { TokenUsage = totalUsage, RawLlmResponse = firstResponse };
    }

    public Task<int?> TryGetContextWindowTokensAsync(string modelName, CancellationToken ct) =>
        new OpenAiModelContextProbe(options, logger).TryGetContextWindowTokensAsync(modelName, ct);

    public async Task<string> CompleteRawAsync(PromptPayload prompt, CancellationToken ct, string phase = "review")
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);

        var (content, _) = await SendAsync(prompt, [prompt.UserPrompt], OpenAiResponseFormats.Text, includeContextRequestsInJsonSchema: false, phase, ct);
        return content;
    }

    public IReviewLlm WithModelName(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        return new OpenAiReviewLlm(
            options with { ModelName = modelName },
            logger,
            configuredClient ?? sdkClient,
            delayAsync);
    }

    private async Task<(string Content, LlmTokenUsage? Usage)> SendAsync(
        PromptPayload prompt,
        IReadOnlyList<string> userMessages,
        string responseFormat,
        bool includeContextRequestsInJsonSchema,
        string phase,
        CancellationToken ct)
    {
        var request = new OpenAiChatRequest(
            SystemPrompt: prompt.SystemPrompt,
            UserMessages: userMessages,
            ModelName: options.ModelName,
            MaxTokens: options.MaxTokens,
            Temperature: options.Temperature,
            ResponseFormat: responseFormat,
            IncludeContextRequestsInJsonSchema: includeContextRequestsInJsonSchema);

        for (var retryAttempt = 0; ; retryAttempt++)
        {
            try
            {
                var response = await GetClient().CompleteChatAsync(request, ct).ConfigureAwait(false);
                if (response.Usage is not null)
                {
                    ReviewBotLlmMetrics.RecordTokenUsage(ProviderName, phase, response.Usage);
                    if (response.Usage.CachedPromptTokens > 0)
                    {
                        logger.LogDebug(
                            "OpenAI-compatible response reported {CachedTokens} cached prompt tokens for phase {Phase}",
                            response.Usage.CachedPromptTokens,
                            phase);
                    }
                }

                return (response.Content, response.Usage);
            }
            catch (HttpRequestException ex) when (retryAttempt < TransientRetryDelays.Length)
            {
                var delay = TransientRetryDelays[retryAttempt];
                logger.LogWarning(
                    ex,
                    "Transient OpenAI-compatible request failure; retrying after {RetryDelay}",
                    delay);
                await delayAsync(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private IOpenAiChatClient GetClient() =>
        configuredClient ?? (sdkClient ??= new OpenAiSdkChatClient(options, logger));

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
