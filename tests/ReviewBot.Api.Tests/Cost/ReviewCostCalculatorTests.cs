using FluentAssertions;
using Microsoft.Extensions.Options;
using ReviewBot.Api.Cost;
using ReviewBot.Core.Llm;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace ReviewBot.Api.Tests.Cost;

public class ReviewCostCalculatorTests
{
    [Fact]
    public void ComputeCostUsd_ReturnsCorrectCostForKnownModel()
    {
        var options = OptionsFor(new Dictionary<string, CostRate>
        {
            ["claude-sonnet-4-6"] = new() { InputPer1M = 3.00m, OutputPer1M = 15.00m }
        });
        var calculator = new ReviewCostCalculator(options);
        var usage = new LlmTokenUsage(PromptTokens: 500_000, CompletionTokens: 100_000);

        var cost = calculator.ComputeCostUsd("claude-sonnet-4-6", usage);

        // 0.5M * $3 + 0.1M * $15 = $1.50 + $1.50 = $3.00
        cost.Should().Be(3.00m);
    }

    [Fact]
    public void ComputeCostUsd_ReturnsZeroWhenBothRatesAreZero()
    {
        var options = OptionsFor(new Dictionary<string, CostRate>
        {
            ["ollama-local"] = new() { InputPer1M = 0m, OutputPer1M = 0m }
        });
        var calculator = new ReviewCostCalculator(options);
        var usage = new LlmTokenUsage(PromptTokens: 1_000, CompletionTokens: 500);

        var cost = calculator.ComputeCostUsd("ollama-local", usage);

        cost.Should().Be(0m);
    }

    [Fact]
    public void ComputeCostUsd_ReturnsNullWhenModelNotConfigured()
    {
        var options = OptionsFor([]);
        var calculator = new ReviewCostCalculator(options);
        var usage = new LlmTokenUsage(PromptTokens: 1_000, CompletionTokens: 500);

        var cost = calculator.ComputeCostUsd("unknown-model", usage);

        cost.Should().BeNull();
    }

    [Fact]
    public void ComputeCostUsd_OnlyCountsPromptAndCompletionTokens_NotCached()
    {
        var options = OptionsFor(new Dictionary<string, CostRate>
        {
            ["claude-opus-4-7"] = new() { InputPer1M = 15.00m, OutputPer1M = 75.00m }
        });
        var calculator = new ReviewCostCalculator(options);
        // Cached tokens are not charged separately by the calculator — the provider bills them differently
        var usage = new LlmTokenUsage(PromptTokens: 1_000_000, CompletionTokens: 1_000_000, CachedPromptTokens: 999_999);

        var cost = calculator.ComputeCostUsd("claude-opus-4-7", usage);

        // 1M * $15 + 1M * $75 = $90
        cost.Should().Be(90.00m);
    }

    [Fact]
    public void ComputeCostUsd_ThrowsOnNullUsage()
    {
        var options = OptionsFor([]);
        var calculator = new ReviewCostCalculator(options);

        var act = () => calculator.ComputeCostUsd("any-model", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ComputeCostUsd_UsesExactTokenCounts()
    {
        var options = OptionsFor(new Dictionary<string, CostRate>
        {
            ["gpt-5.1"] = new() { InputPer1M = 2.00m, OutputPer1M = 8.00m }
        });
        var calculator = new ReviewCostCalculator(options);
        var usage = new LlmTokenUsage(PromptTokens: 1, CompletionTokens: 1);

        var cost = calculator.ComputeCostUsd("gpt-5.1", usage);

        // (1 / 1_000_000) * 2 + (1 / 1_000_000) * 8 = $0.000010
        cost.Should().Be(0.000010m);
    }

    private static IOptions<CostRateOptions> OptionsFor(Dictionary<string, CostRate> rates) =>
        MsOptions.Create(new CostRateOptions { Rates = rates });
}
