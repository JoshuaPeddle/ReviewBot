using FluentAssertions;
using ReviewBot.Core.Context;

namespace ReviewBot.Core.Tests.Context;

public class HeuristicTokenEstimatorTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("hello world", 4)]
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

        tokens.Should().Be((int)Math.Ceiling(source.Length / 3d));
    }
}
