using FluentAssertions;
using ReviewBot.Llm.OpenAi;

namespace ReviewBot.Llm.Tests.OpenAi;

public sealed class OpenAiContextLimitFitterTests
{
    [Fact]
    public void HalvesOutputTokensOnVllmContextLengthOverflow()
    {
        // Exact shape vLLM 0.23 returns when prompt + output overflow the window.
        // The "16485 input tokens" figure is a lower bound derived from the output
        // ask, so we deliberately ignore it and just halve the output allowance.
        var body =
            "{\"error\":{\"message\":\"This model's maximum context length is 32768 tokens. "
            + "However, you requested 16284 output tokens and your prompt contains at least 16485 input tokens, "
            + "for a total of at least 32769 tokens.\",\"type\":\"BadRequestError\",\"code\":400}}";

        var fitted = OpenAiContextLimitFitter.TryFitMaxOutputTokens(body, 16284, out var value);

        fitted.Should().BeTrue();
        value.Should().Be(16284 / 2);
    }

    [Fact]
    public void RecognisesAlternateMaxModelLenPhrasing()
    {
        var body =
            "{\"error\":{\"message\":\"max_tokens=4096 cannot be greater than max_model_len=max_total_tokens=32768.\"}}";

        var fitted = OpenAiContextLimitFitter.TryFitMaxOutputTokens(body, 4096, out var value);

        fitted.Should().BeTrue();
        value.Should().Be(2048);
    }

    [Fact]
    public void ConvergesAfterRepeatedHalving()
    {
        // Simulate the retry loop: keep halving while the server keeps rejecting.
        const string body = "This model's maximum context length is 32768 tokens.";
        var max = 16284;

        for (var i = 0; i < 4; i++)
        {
            OpenAiContextLimitFitter.TryFitMaxOutputTokens(body, max, out max).Should().BeTrue();
        }

        max.Should().Be(16284 / 2 / 2 / 2 / 2);
    }

    [Fact]
    public void StopsAtFloorSoOutputStaysUseful()
    {
        const string body = "This model's maximum context length is 32768 tokens.";

        // 600 -> 300 would fall below the 512 floor, so refit declines.
        var fitted = OpenAiContextLimitFitter.TryFitMaxOutputTokens(body, 600, out var value);

        fitted.Should().BeFalse();
        value.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some unrelated 400 about a bad parameter")]
    [InlineData("rate limit exceeded")]
    public void DoesNotFitUnrecognisedBodies(string? body)
    {
        var fitted = OpenAiContextLimitFitter.TryFitMaxOutputTokens(body, 16284, out var value);

        fitted.Should().BeFalse();
        value.Should().Be(0);
    }
}
