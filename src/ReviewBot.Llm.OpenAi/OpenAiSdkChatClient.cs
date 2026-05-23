using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace ReviewBot.Llm.OpenAi;

internal sealed class OpenAiSdkChatClient : IOpenAiChatClient
{
    private readonly ApiKeyCredential credential;
    private readonly OpenAIClientOptions? clientOptions;

    public OpenAiSdkChatClient(OpenAiLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new ArgumentException("OpenAI API key must be configured.", nameof(options));
        }

        credential = new ApiKeyCredential(options.ApiKey);
        clientOptions = CreateClientOptions(options.BaseUrl);
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
            ResponseFormat = request.UseJsonMode
                ? ChatResponseFormat.CreateJsonObjectFormat()
                : null
        };

        var completion = await client.CompleteChatAsync(messages, options, ct);
        var textParts = completion.Value.Content
            .Where(part => !string.IsNullOrEmpty(part.Text))
            .Select(part => part.Text);

        return string.Concat(textParts);
    }

    internal static OpenAIClientOptions? CreateClientOptions(Uri? baseUrl) =>
        baseUrl is null
            ? null
            : new OpenAIClientOptions { Endpoint = baseUrl };
}
