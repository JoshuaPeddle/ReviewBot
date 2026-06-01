using FluentAssertions;
using ReviewBot.Core.Context;
using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Tests.Context;

public sealed class ReviewPromptTokenEstimatorTests
{
    [Fact]
    public void EstimateTokensUsesProviderSpecificEstimatorWhenRegistered()
    {
        var estimator = new ReviewPromptTokenEstimator(
            new FixedTokenEstimator(3),
            [new FixedProviderTokenEstimator("anthropic", 17)]);

        var tokens = estimator.EstimateTokens(new ModelConfig("anthropic", "claude-test", null), "hello");

        tokens.Should().Be(17);
    }

    [Fact]
    public void EstimateTokensFallsBackToHeuristicEstimatorForUnknownProvider()
    {
        var estimator = new ReviewPromptTokenEstimator(
            new FixedTokenEstimator(3),
            [new FixedProviderTokenEstimator("anthropic", 17)]);

        var tokens = estimator.EstimateTokens(new ModelConfig("openai", "qwen-test", null), "hello");

        tokens.Should().Be(3);
    }

    private sealed class FixedTokenEstimator(int tokens) : IPromptTokenEstimator
    {
        public int EstimateTokens(string? text) => tokens;
    }

    private sealed class FixedProviderTokenEstimator(string providerName, int tokens) : IProviderPromptTokenEstimator
    {
        public string ProviderName { get; } = providerName;

        public int EstimateTokens(ModelConfig model, string? text) => tokens;
    }
}
