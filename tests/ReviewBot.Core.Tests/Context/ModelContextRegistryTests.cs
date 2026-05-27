using FluentAssertions;
using ReviewBot.Core.Context;

namespace ReviewBot.Core.Tests.Context;

public class ModelContextRegistryTests
{
    [Theory]
    [InlineData("claude-opus-4-7", 200_000)]
    [InlineData("gpt-4.1", 128_000)]
    [InlineData("gpt-5.1", 128_000)]
    [InlineData("qwen2.5:9b-q4_K_M", 32_768)]
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
}
