using Microsoft.Extensions.Logging;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;
using ReviewBot.Core.Prompting;

namespace ReviewBot.Llm.OpenAi;

public sealed class OpenAiReviewLlm : IConfigurableReviewLlm
{
    private const string RetryInstruction = "Your previous response was not valid JSON. Respond again with ONLY the JSON object.";
    public string ProviderName => "openai";

    private readonly OpenAiLlmOptions options;
    private readonly ILogger<OpenAiReviewLlm> logger;
    private readonly IOpenAiChatClient? configuredClient;
    private IOpenAiChatClient? sdkClient;

    public OpenAiReviewLlm(OpenAiLlmOptions options, ILogger<OpenAiReviewLlm> logger)
        : this(options, logger, null)
    {
    }

    internal OpenAiReviewLlm(
        OpenAiLlmOptions options,
        ILogger<OpenAiReviewLlm> logger,
        IOpenAiChatClient? client)
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

        logger.LogWarning("OpenAI-compatible response was not valid review JSON; retrying once. Error: {Error}", firstParse.Error);

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

        return new OpenAiReviewLlm(
            options with { ModelName = modelName },
            logger,
            configuredClient ?? sdkClient);
    }

    private Task<string> SendAsync(PromptPayload prompt, IReadOnlyList<string> userMessages, CancellationToken ct) =>
        GetClient().CompleteChatAsync(
            new OpenAiChatRequest(
                SystemPrompt: prompt.SystemPrompt,
                UserMessages: userMessages,
                ModelName: options.ModelName,
                MaxTokens: options.MaxTokens,
                Temperature: options.Temperature,
                UseJsonMode: options.UseJsonMode),
            ct);

    private IOpenAiChatClient GetClient() =>
        configuredClient ?? (sdkClient ??= new OpenAiSdkChatClient(options));
}
