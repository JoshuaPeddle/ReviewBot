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
    private readonly ILogger? logger;

    public OpenAiSdkChatClient(OpenAiLlmOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new ArgumentException("OpenAI API key must be configured.", nameof(options));
        }

        credential = new ApiKeyCredential(options.ApiKey);
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

            try
            {
                var completion = await client.CompleteChatAsync(messages, options, ct);
                var textParts = completion.Value.Content
                    .Where(part => !string.IsNullOrEmpty(part.Text))
                    .Select(part => part.Text);

                var usage = completion.Value.Usage is null
                    ? null
                    : new LlmTokenUsage(
                        PromptTokens: completion.Value.Usage.InputTokenCount,
                        CompletionTokens: completion.Value.Usage.OutputTokenCount,
                        CachedPromptTokens: completion.Value.Usage.InputTokenDetails?.CachedTokenCount ?? 0);

                return new OpenAiChatResult(string.Concat(textParts), usage);
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

                throw new OpenAiChatRequestException(ex.Status, body, ex);
            }
        }
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
