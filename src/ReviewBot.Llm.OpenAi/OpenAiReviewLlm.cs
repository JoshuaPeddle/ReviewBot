using Microsoft.Extensions.Logging;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;
using ReviewBot.Core.Prompting;

namespace ReviewBot.Llm.OpenAi;

public sealed class OpenAiReviewLlm : IConfigurableReviewLlm, IModelContextProbe
{
    private const int MaxLoggedRawResponseLength = 500;

    // How many times we may double the output allowance when a reasoning model spends
    // the whole thing thinking and returns nothing.
    //
    // One. A model that merely lacked room finishes at some size, so a single doubling
    // settles the question; if the doubled attempt also consumes every token and still
    // says nothing, the generation is not converging and each further doubling spends a
    // whole extra generation to learn that again. Measured reviewing a 5-file chunk of
    // this repo on Qwen3.6-27B: 12,500 then 25,000 then 50,000 completion tokens, every
    // one of them 100% consumed with no content — 87,500 tokens to fail. Stopping after
    // the first expansion caps the same failure at 37,500.
    private const int MaxEmptyResponseExpansions = 1;

    // An expanded allowance is never grown past this, however large the starting reserve.
    // ContextBudget already treats a response reserve above a quarter of the window as
    // unreasonable (MaxReserveContextDivisor), and doubling used to sail straight through
    // that: on a 100K model the third attempt asked for 50,000 output tokens — half the
    // entire window — for a reply that is a few thousand tokens of JSON. This only ever
    // caps growth; a caller that deliberately configures a larger allowance keeps it.
    private const int MaxExpandedOutputTokens = 32_000;

    // Providers do not always report completion tokens exactly equal to the allowance
    // when they truncate, so treat "near the ceiling" as having hit it.
    private const double OutputAllowanceExhaustedFraction = 0.9;

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
        // Prefer the budget-derived output allowance so prompt + output fits the
        // model context window; fall back to the host default when unset.
        var maxOutputTokens = request.MaxOutputTokens is > 0 ? request.MaxOutputTokens.Value : options.MaxTokens;
        var (firstResponse, firstUsage) = await SendWithOutputExpansionAsync(
            prompt, responseFormat, includeContextRequests, "review", maxOutputTokens, ct);
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
        var (repairResponse, repairUsage) = await SendAsync(repairPrompt, [repairPrompt.UserPrompt], responseFormat, includeContextRequests, "review", maxOutputTokens, ct);
        var totalUsage = firstUsage?.Add(repairUsage) ?? repairUsage;
        var repairParse = LlmResultParser.Parse(repairResponse, logger);
        if (repairParse is { Success: true, Value: not null })
        {
            ReviewBotLlmMetrics.RecordParseFailure(ProviderName, repaired: true);
            return repairParse.Value with { TokenUsage = totalUsage, RawLlmResponse = firstResponse };
        }

        logger.LogWarning(
            "OpenAI-compatible response repair failed. Error: {Error}; RawResponse: {RawResponse}",
            repairParse.Error,
            Truncate(repairResponse, MaxLoggedRawResponseLength));
        ReviewBotLlmMetrics.RecordParseFailure(ProviderName, repaired: false);
        // Deliberately fail rather than return an empty result: an empty result is posted
        // as a clean review, which reads as "no issues found" on a PR nothing reviewed.
        throw new LlmResponseUnusableException(
            $"{ProviderName} response could not be parsed as review JSON, and the repair pass "
            + $"failed as well. Last parse error: {repairParse.Error}");
    }

    public Task<int?> TryGetContextWindowTokensAsync(string modelName, CancellationToken ct) =>
        new OpenAiModelContextProbe(options, logger).TryGetContextWindowTokensAsync(modelName, ct);

    public async Task<string> CompleteRawAsync(PromptPayload prompt, CancellationToken ct, string phase = "review")
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);

        var (content, _) = await SendAsync(prompt, [prompt.UserPrompt], OpenAiResponseFormats.Text, includeContextRequestsInJsonSchema: false, phase, options.MaxTokens, ct);
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

    /// <summary>
    /// Sends a prompt, growing the output allowance when the model spends all of it
    /// reasoning and returns no content.
    /// </summary>
    /// <remarks>
    /// A reasoning model streams chain-of-thought before it answers, so an allowance
    /// that is merely small produces an empty body rather than a truncated one. Retrying
    /// at the same size would fail identically, and no fixed default is right for every
    /// diff — a large review legitimately needs more thinking room than a one-line one.
    /// Observed on Qwen3.6-27B, which exhausted 4096 and then 12500 tokens on the same PR.
    ///
    /// If a doubled allowance no longer fits the context window the server rejects the
    /// request and <see cref="OpenAiContextLimitFitter"/> refits it, so this cannot wedge
    /// the two mechanisms against each other.
    /// </remarks>
    private async Task<(string Content, LlmTokenUsage? Usage)> SendWithOutputExpansionAsync(
        PromptPayload prompt,
        string responseFormat,
        bool includeContextRequestsInJsonSchema,
        string phase,
        int maxOutputTokens,
        CancellationToken ct)
    {
        var allowance = maxOutputTokens;
        LlmTokenUsage? cumulativeUsage = null;

        for (var attempt = 0; ; attempt++)
        {
            var (content, usage) = await SendAsync(
                prompt, [prompt.UserPrompt], responseFormat, includeContextRequestsInJsonSchema, phase, allowance, ct)
                .ConfigureAwait(false);
            cumulativeUsage = cumulativeUsage?.Add(usage) ?? usage;

            if (!string.IsNullOrWhiteSpace(content))
            {
                return (content, cumulativeUsage);
            }

            var consumed = usage?.CompletionTokens ?? 0;
            var exhaustedAllowance = consumed >= allowance * OutputAllowanceExhaustedFraction;
            var expanded = Math.Min(allowance * 2, MaxExpandedOutputTokens);
            var canGrow = expanded > allowance;
            if (attempt >= MaxEmptyResponseExpansions || !exhaustedAllowance || !canGrow)
            {
                // Either we have grown the allowance as far as we are willing to, the
                // model stopped early for some other reason so more room would not help,
                // or the allowance is already at the ceiling.
                EmptyLlmResponse.ThrowIfUnusable(
                    content, ProviderName, consumed, allowance, allowanceWasExpanded: attempt > 0);
            }

            logger.LogWarning(
                "{Provider} consumed its entire {Allowance}-token output allowance without emitting content "
                + "(reasoning model out of room); retrying once with {NewAllowance}",
                ProviderName,
                allowance,
                expanded);
            allowance = expanded;
        }
    }

    private async Task<(string Content, LlmTokenUsage? Usage)> SendAsync(
        PromptPayload prompt,
        IReadOnlyList<string> userMessages,
        string responseFormat,
        bool includeContextRequestsInJsonSchema,
        string phase,
        int maxOutputTokens,
        CancellationToken ct)
    {
        var request = new OpenAiChatRequest(
            SystemPrompt: prompt.SystemPrompt,
            UserMessages: userMessages,
            ModelName: options.ModelName,
            MaxTokens: maxOutputTokens,
            Temperature: options.Temperature,
            ResponseFormat: responseFormat,
            IncludeContextRequestsInJsonSchema: includeContextRequestsInJsonSchema,
            Sampling: options.Sampling);

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
