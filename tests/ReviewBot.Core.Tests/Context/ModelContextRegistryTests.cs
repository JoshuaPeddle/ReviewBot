using FluentAssertions;
using Microsoft.Extensions.Logging;
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
    public void ConstructorLogsWarningAndIgnoresInvalidConfiguredLimits()
    {
        var logger = new CapturingLogger<ModelContextRegistry>();
        var registry = new ModelContextRegistry(
            new ModelContextOptions
            {
                Limits =
                {
                    ["qwen2.5:9b-q4_K_M"] = 0,
                    [" "] = 16_384
                }
            },
            logger);

        var tokens = registry.GetContextWindowTokens("qwen2.5:9b-q4_K_M");

        tokens.Should().Be(32_768);
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("Ignoring invalid ModelContext limit", StringComparison.Ordinal));
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("blank model pattern", StringComparison.Ordinal));
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

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
