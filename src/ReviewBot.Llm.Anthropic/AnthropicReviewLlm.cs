using Microsoft.Extensions.Logging;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;
using ReviewBot.Core.Prompting;

namespace ReviewBot.Llm.Anthropic;

public sealed class AnthropicReviewLlm : IReviewLlm
{
    private const string RetryInstruction = "Your previous response was not valid JSON. Respond again with ONLY the JSON object.";

    private readonly AnthropicLlmOptions options;
    private readonly ILogger<AnthropicReviewLlm> logger;
    private readonly IAnthropicClient client;

    public AnthropicReviewLlm(
        AnthropicLlmOptions options,
        ILogger<AnthropicReviewLlm> logger,
        HttpClient? httpClient = null)
        : this(options, logger, new AnthropicSdkClient(options, httpClient))
    {
    }

    internal AnthropicReviewLlm(
        AnthropicLlmOptions options,
        ILogger<AnthropicReviewLlm> logger,
        IAnthropicClient client)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(client);

        this.options = options;
        this.logger = logger;
        this.client = client;
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

    private Task<string> SendAsync(PromptPayload prompt, IReadOnlyList<string> userMessages, CancellationToken ct) =>
        client.CreateMessageAsync(
            new AnthropicMessageRequest(
                SystemPrompt: prompt.SystemPrompt,
                UserMessages: userMessages,
                ModelName: options.ModelName,
                MaxTokens: options.MaxTokens,
                Temperature: options.Temperature),
            ct);
}
