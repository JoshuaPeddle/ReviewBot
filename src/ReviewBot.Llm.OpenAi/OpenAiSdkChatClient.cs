using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace ReviewBot.Llm.OpenAi;

internal sealed class OpenAiSdkChatClient : IOpenAiChatClient
{
    private readonly ApiKeyCredential credential;
    private readonly OpenAIClientOptions clientOptions;

    public OpenAiSdkChatClient(OpenAiLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new ArgumentException("OpenAI API key must be configured.", nameof(options));
        }

        credential = new ApiKeyCredential(options.ApiKey);
        clientOptions = CreateClientOptions(options.BaseUrl, options.TimeoutSeconds);
    }

    public async Task<string> CompleteChatAsync(OpenAiChatRequest request, CancellationToken ct)
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

        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = request.MaxTokens,
            Temperature = request.Temperature,
            ResponseFormat = CreateResponseFormat(request.ResponseFormat, request.IncludeContextRequestsInJsonSchema)
        };

        var completion = await client.CompleteChatAsync(messages, options, ct);
        var textParts = completion.Value.Content
            .Where(part => !string.IsNullOrEmpty(part.Text))
            .Select(part => part.Text);

        return string.Concat(textParts);
    }

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

    internal static string BuildReviewJsonSchema(bool includeContextRequests)
    {
        var contextRequestsSchema = includeContextRequests
            ? """
              ,
                    "context_requests": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "path": { "type": "string" },
                          "reason": { "type": "string" }
                        },
                        "required": ["path"],
                        "additionalProperties": false
                      }
                    }
              """
            : string.Empty;

        return $$"""
               {
                 "type": "object",
                 "properties": {
                   "summary": { "type": "string" },
                   "comments": {
                     "type": "array",
                     "items": {
                       "type": "object",
                       "properties": {
                         "path": { "type": "string" },
                         "line": { "type": "integer" },
                         "severity": { "type": "string", "enum": ["info", "warning", "error"] },
                         "confidence": { "type": "string", "enum": ["high", "medium", "low"] },
                         "body": { "type": "string" }
                       },
                       "required": ["path", "line", "severity", "confidence", "body"],
                       "additionalProperties": false
                     }
                   }{{contextRequestsSchema}}
                 },
                 "required": ["summary", "comments"],
                 "additionalProperties": false
               }
               """;
    }
}
