using Microsoft.Extensions.Logging;
using ReviewBot.Core.Context;
using ReviewBot.Core.Domain;

namespace ReviewBot.Llm.Anthropic;

internal sealed class AnthropicTokenEstimator : IProviderPromptTokenEstimator
{
    private readonly AnthropicLlmOptions options;
    private readonly IPromptTokenEstimator heuristic;
    private readonly ILogger<AnthropicTokenEstimator> logger;
    private readonly IAnthropicClient? configuredClient;
    private IAnthropicClient? sdkClient;

    public AnthropicTokenEstimator(
        AnthropicLlmOptions options,
        IPromptTokenEstimator heuristic,
        ILogger<AnthropicTokenEstimator> logger)
        : this(options, heuristic, logger, client: null)
    {
    }

    internal AnthropicTokenEstimator(
        AnthropicLlmOptions options,
        IPromptTokenEstimator heuristic,
        ILogger<AnthropicTokenEstimator> logger,
        IAnthropicClient? client)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.heuristic = heuristic ?? throw new ArgumentNullException(nameof(heuristic));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        configuredClient = client;
    }

    public string ProviderName => "anthropic";

    public int EstimateTokens(ModelConfig model, string? text)
    {
        ArgumentNullException.ThrowIfNull(model);

        var heuristicTokens = heuristic.EstimateTokens(text);
        if (!options.TokenCountingEnabled ||
            heuristicTokens < options.TokenCountingHeuristicThresholdTokens ||
            string.IsNullOrEmpty(text))
        {
            return heuristicTokens;
        }

        try
        {
            return GetClient()
                .CountTokensAsync(
                    new AnthropicTokenCountRequest(
                        ModelName: model.Name,
                        SystemPrompt: null,
                        UserMessages: [text]),
                    CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Anthropic token counting failed for model {ModelName}; using heuristic estimate {HeuristicTokens}",
                model.Name,
                heuristicTokens);
            return heuristicTokens;
        }
    }

    private IAnthropicClient GetClient() =>
        configuredClient ?? (sdkClient ??= new AnthropicSdkClient(options));
}
