using System.Text;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using Microsoft.Extensions.Logging;
using ReviewBot.Core.Llm;

namespace ReviewBot.Llm.OpenAi;

internal sealed class OpenAiSdkChatClient : IOpenAiChatClient
{
    private const int MaxLoggedBodyLength = 500;

    // How many times we may halve the output allowance to fit the model context
    // before giving up. Four halvings shrink the request by 16x, enough to cross
    // from "half the window" down to the floor for any realistic prompt.
    private const int MaxContextRefitRetries = 4;

    private readonly ApiKeyCredential credential;
    private readonly OpenAIClientOptions clientOptions;
    private readonly Uri? baseUrl;
    private readonly bool streaming;
    private readonly ILogger? logger;

    public OpenAiSdkChatClient(OpenAiLlmOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new ArgumentException("OpenAI API key must be configured.", nameof(options));
        }

        credential = new ApiKeyCredential(options.ApiKey);
        baseUrl = options.BaseUrl;
        streaming = options.Streaming;
        clientOptions = CreateClientOptions(options.BaseUrl, options.TimeoutSeconds);
        this.logger = logger;
    }

    public async Task<OpenAiChatResult> CompleteChatAsync(OpenAiChatRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var client = new ChatClient(
            model: request.ModelName,
            credential: credential,
            options: clientOptions);
        var messages = request.UserMessages
            .Select<string, ChatMessage>(userMessage => new UserChatMessage(userMessage))
            .Prepend(new SystemChatMessage(request.SystemPrompt))
            .ToList();

        var maxOutputTokens = request.MaxTokens;

        // Attempt 0 is the request as budgeted; attempt 1 is a single retry with a
        // smaller output allowance if the server rejected attempt 0 because prompt
        // + output overflowed the model context window.
        for (var attempt = 0; ; attempt++)
        {
            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = maxOutputTokens,
                Temperature = request.Temperature,
                ResponseFormat = CreateResponseFormat(request.ResponseFormat, request.IncludeContextRequestsInJsonSchema),
            };
            ApplySampling(options, request.Sampling);

            try
            {
                return streaming
                    ? await CompleteStreamingAsync(client, messages, options, ct).ConfigureAwait(false)
                    : await CompleteBufferedAsync(client, messages, options, ct).ConfigureAwait(false);
            }
            catch (ClientResultException ex) when (ex.Status == 400)
            {
                var body = TryReadResponseBody(ex);
                if (attempt < MaxContextRefitRetries &&
                    OpenAiContextLimitFitter.TryFitMaxOutputTokens(body, maxOutputTokens, out var fitted))
                {
                    logger?.LogWarning(
                        "OpenAI-compatible server rejected the request as too long for the model context; "
                        + "refitting max output tokens {OldMaxTokens} -> {NewMaxTokens} and retrying once. Server reported: {Error}",
                        maxOutputTokens,
                        fitted,
                        Truncate(body, MaxLoggedBodyLength));
                    maxOutputTokens = fitted;
                    continue;
                }

                throw new OpenAiChatRequestException(ex.Status, body, ex, baseUrl, request.ModelName);
            }
            catch (ClientResultException ex)
            {
                // Everything else used to surface as the SDK's bare "Service request
                // failed", which named neither the endpoint nor the model — a stale base
                // URL read as an unexplained dead review job.
                throw new OpenAiChatRequestException(
                    ex.Status, TryReadResponseBody(ex), ex, baseUrl, request.ModelName);
            }
        }
    }

    private static async Task<OpenAiChatResult> CompleteBufferedAsync(
        ChatClient client,
        IReadOnlyList<ChatMessage> messages,
        ChatCompletionOptions options,
        CancellationToken ct)
    {
        var completion = await client.CompleteChatAsync(messages, options, ct).ConfigureAwait(false);
        var textParts = completion.Value.Content
            .Where(part => !string.IsNullOrEmpty(part.Text))
            .Select(part => part.Text);

        return new OpenAiChatResult(string.Concat(textParts), ToUsage(completion.Value.Usage));
    }

    /// <summary>
    /// Streams the completion, accumulating content deltas.
    /// </summary>
    /// <remarks>
    /// A reasoning model can think for minutes before emitting its first content token.
    /// On a buffered request nothing crosses the wire during that time, so any proxy in
    /// front of the model closes the idle connection — observed as HTTP 524 from the
    /// Cloudflare-fronted endpoint on the three largest eval fixtures, which aborted
    /// roughly 5% of fixture runs while our own client timeout (600s) was nowhere near
    /// expiring. Streaming keeps bytes moving, so the proxy sees a live connection.
    /// </remarks>
    private static async Task<OpenAiChatResult> CompleteStreamingAsync(
        ChatClient client,
        IReadOnlyList<ChatMessage> messages,
        ChatCompletionOptions options,
        CancellationToken ct)
    {
        // Usage only arrives on the final chunk when explicitly requested, and the SDK
        // exposes no typed property for it, so patch it into the request body.
#pragma warning disable SCME0001
        options.Patch.Set("$.stream_options.include_usage"u8, true);
#pragma warning restore SCME0001

        var content = new StringBuilder();
        ChatTokenUsage? usage = null;

        await foreach (var update in client.CompleteChatStreamingAsync(messages, options, ct).ConfigureAwait(false))
        {
            foreach (var part in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(part.Text))
                {
                    content.Append(part.Text);
                }
            }

            // Whichever chunk carries usage wins; servers differ on which one that is.
            usage ??= update.Usage;
        }

        return new OpenAiChatResult(content.ToString(), ToUsage(usage));
    }

    private static LlmTokenUsage? ToUsage(ChatTokenUsage? usage) =>
        usage is null
            ? null
            : new LlmTokenUsage(
                PromptTokens: usage.InputTokenCount,
                CompletionTokens: usage.OutputTokenCount,
                CachedPromptTokens: usage.InputTokenDetails?.CachedTokenCount ?? 0);

    /// <summary>
    /// Copies the configured sampling knobs onto the outgoing request. Knobs the OpenAI
    /// chat schema models get a typed property; the rest are written straight into the
    /// request body, which is how local servers (vLLM, SGLang, Ollama) accept them.
    /// </summary>
    internal static void ApplySampling(ChatCompletionOptions options, OpenAiSamplingOptions? sampling)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (sampling is null)
        {
            return;
        }

        if (sampling.TopP is { } topP)
        {
            options.TopP = topP;
        }

        if (sampling.PresencePenalty is { } presencePenalty)
        {
            options.PresencePenalty = presencePenalty;
        }

        // Seed is still an evaluation-only surface in the SDK; scope the suppression to
        // the one line that touches it.
#pragma warning disable OPENAI001
        if (sampling.Seed is { } seed)
        {
            options.Seed = seed;
        }
#pragma warning restore OPENAI001

        // JsonPatch is the SDK's supported escape hatch for fields outside the OpenAI
        // schema. It is marked experimental, so the suppression is scoped to these three
        // lines: if the API changes, the build fails here and nowhere else.
#pragma warning disable SCME0001
        if (sampling.TopK is { } topK)
        {
            options.Patch.Set("$.top_k"u8, topK);
        }

        if (sampling.MinP is { } minP)
        {
            options.Patch.Set("$.min_p"u8, minP);
        }

        if (sampling.RepetitionPenalty is { } repetitionPenalty)
        {
            options.Patch.Set("$.repetition_penalty"u8, repetitionPenalty);
        }
#pragma warning restore SCME0001
    }

    private static string? TryReadResponseBody(ClientResultException ex)
    {
        try
        {
            return ex.GetRawResponse()?.Content?.ToString();
        }
        catch (InvalidOperationException)
        {
            // Some pipeline responses don't buffer content; fall back to the SDK message.
            return ex.Message;
        }
    }

    private static string Truncate(string? value, int maxLength) =>
        value is null ? string.Empty :
        value.Length <= maxLength ? value : value[..maxLength];

    internal static OpenAIClientOptions CreateClientOptions(Uri? baseUrl, int timeoutSeconds) =>
        new()
        {
            Endpoint = baseUrl,
            NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds)
        };

    internal static ChatResponseFormat? CreateResponseFormat(string responseFormat, bool includeContextRequests)
    {
        var normalized = OpenAiResponseFormats.Normalize(responseFormat);
        return normalized switch
        {
            OpenAiResponseFormats.JsonObject => ChatResponseFormat.CreateJsonObjectFormat(),
            OpenAiResponseFormats.JsonSchema => ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "review_response",
                jsonSchema: BinaryData.FromString(BuildReviewJsonSchema(includeContextRequests)),
                jsonSchemaIsStrict: false),
            OpenAiResponseFormats.Text => null,
            _ => throw new InvalidOperationException($"Unexpected OpenAI response format '{normalized}'."),
        };
    }

    internal static string BuildReviewJsonSchema(bool includeContextRequests) =>
        ReviewJsonSchema.Build(includeContextRequests);
}
