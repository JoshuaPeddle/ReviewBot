using FluentAssertions;
using ReviewBot.Core.Context;

namespace ReviewBot.Core.Tests.Context;

public class HeuristicTokenEstimatorTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    // CharactersPerToken = 2.5: ceil(11 / 2.5) = 5; ceil(7 / 2.5) = 3.
    [InlineData("hello world", 5)]
    [InlineData("1234567", 3)]
    public void EstimateTokensUsesCeilingCharacterHeuristic(string? text, int expectedTokens)
    {
        var estimator = new HeuristicTokenEstimator();

        var tokens = estimator.EstimateTokens(text);

        tokens.Should().Be(expectedTokens);
    }

    [Fact]
    public void EstimateTokensHandlesSourceCodeSamples()
    {
        const string source = """
            public sealed class Review
            {
                public string Summary { get; init; } = string.Empty;
            }
            """;
        var estimator = new HeuristicTokenEstimator();

        var tokens = estimator.EstimateTokens(source);

        tokens.Should().Be((int)Math.Ceiling(source.Length / 2.5));
    }
}
