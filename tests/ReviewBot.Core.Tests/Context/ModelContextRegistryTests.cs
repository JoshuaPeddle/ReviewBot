using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ReviewBot.Core.Context;

namespace ReviewBot.Core.Tests.Context;

public class ModelContextRegistryTests
{
    [Theory]
    [InlineData("claude-opus-4-7", 200_000)]
    [InlineData("gpt-4.1", 128_000)]
    [InlineData("gpt-5.1", 128_000)]
    [InlineData("qwen2.5:9b-q4_K_M", 32_768)]
    [InlineData("qwen/qwen3.5-9b", 32_768)]
    [InlineData("qwen3.5-9b-q4_K_M", 32_768)]
    [InlineData("qwen3.5-4b@q4_k_xl", 32_768)]
    [InlineData("llama3.1:8b-instruct", 8_192)]
    [InlineData("custom:8b-local", 8_192)]
    [InlineData("llama3.3:70b-instruct", 131_072)]
    [InlineData("custom:70b-local", 131_072)]
    [InlineData("granite3.3:8b", 128_000)]
    public void GetContextWindowTokensReturnsKnownModelDefaults(string model, int expectedTokens)
    {
        var registry = new ModelContextRegistry();

        var tokens = registry.GetContextWindowTokens(model);

        tokens.Should().Be(expectedTokens);
    }

    [Fact]
    public void GetContextWindowTokensUsesConfiguredExactOverride()
    {
        var registry = new ModelContextRegistry(new ModelContextOptions
        {
            Limits =
            {
                ["qwen2.5:9b-q4_K_M"] = 65_536
            }
        });

        var tokens = registry.GetContextWindowTokens("qwen2.5:9b-q4_K_M");

        tokens.Should().Be(65_536);
    }

    [Fact]
    public void ConstructorRejectsInvalidConfiguredLimits()
    {
        var act = () => new ModelContextRegistry(
            new ModelContextOptions
            {
                Limits =
                {
                    ["qwen2.5:9b-q4_K_M"] = 0
                }
            });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ModelContext:Limits:qwen2.5:9b-q4_K_M*between 1 and 2000000*");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("qwen model")]
    [InlineData(" qwen*")]
    public void ConstructorRejectsInvalidConfiguredPatterns(string pattern)
    {
        var act = () => new ModelContextRegistry(new ModelContextOptions
        {
            Limits = { [pattern] = 16_384 }
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ModelContext:Limits*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2_000_001)]
    public void ConstructorRejectsInvalidGlobalCap(int tokens)
    {
        var act = () => new ModelContextRegistry(new ModelContextOptions
        {
            MaxContextWindowTokens = tokens
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ModelContext:MaxContextWindowTokens*between 1 and 2000000*");
    }

    [Fact]
    public void AddPromptBudgetingValidatesConfigurationEagerly()
    {
        var services = new ServiceCollection();

        var act = () => services.AddPromptBudgeting(options =>
            options.MaxContextWindowTokens = 0);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ModelContext:MaxContextWindowTokens*");
    }

    [Fact]
    public void GetContextWindowTokensPrefersLongestLiteralPrefix()
    {
        var registry = new ModelContextRegistry(new ModelContextOptions
        {
            Limits =
            {
                ["qwen*"] = 16_384,
                ["qwen2.5:*"] = 65_536
            }
        });

        var tokens = registry.GetContextWindowTokens("qwen2.5:9b-q4_K_M");

        tokens.Should().Be(65_536);
    }

    [Fact]
    public void GetContextWindowTokensFallsBackForUnknownOrBlankModels()
    {
        var registry = new ModelContextRegistry();

        registry.GetContextWindowTokens("unknown-model").Should().Be(ModelContextRegistry.FallbackContextTokens);
        registry.GetContextWindowTokens("").Should().Be(ModelContextRegistry.FallbackContextTokens);
    }

    [Fact]
    public void ApplyConfiguredCapLimitsLiveProviderValue()
    {
        var registry = new ModelContextRegistry(new ModelContextOptions
        {
            MaxContextWindowTokens = 100_000,
            Limits = { ["qwen/smaller*"] = 80_000 }
        });

        registry.ApplyConfiguredCap("Qwen/Qwen3.6-27B-FP8", 128_000).Should().Be(100_000);
        registry.ApplyConfiguredCap("qwen/smaller-model", 128_000).Should().Be(80_000);
        registry.ApplyConfiguredCap("Qwen/Qwen3.6-27B-FP8", 64_000).Should().Be(64_000);
    }
}
