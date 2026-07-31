using FluentAssertions;
using ReviewBot.Llm.OpenAi;

namespace ReviewBot.Llm.Tests.OpenAi;

public sealed class OpenAiLlmOptionsValidatorTests
{
    [Fact]
    public void RegistrationAcceptsH100ConfigurationWithoutApiKey()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        var act = () => services.AddOpenAiReviewLlm(options =>
        {
            options.ModelName = "Qwen/Qwen3.6-27B-FP8";
            options.BaseUrl = new Uri("https://dpllqyofulel2q-8000.proxy.runpod.net/v1");
            options.MaxTokens = 16_384;
            options.Temperature = 0;
            options.TimeoutSeconds = 600;
            options.MaxConcurrentRequests = 6;
        });

        act.Should().NotThrow();
    }

    [Fact]
    public void RegistrationRejectsBlankModelEagerly()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        var act = () => services.AddOpenAiReviewLlm(options => options.ModelName = " ");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OpenAi:ModelName*");
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("file:///tmp/model")]
    [InlineData("ftp://model.example/v1")]
    public void ValidatorRejectsNonHttpBaseUrls(string value)
    {
        var options = new OpenAiLlmOptions { BaseUrl = new Uri(value, UriKind.RelativeOrAbsolute) };

        var act = () => OpenAiLlmOptionsValidator.Validate(options);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OpenAi:BaseUrl*absolute HTTP(S) URI*");
    }

    [Fact]
    public void ValidatorRejectsInvalidOperationalIntegerValues()
    {
        OpenAiLlmOptions[] invalidOptions =
        [
            new() { MaxTokens = 0 },
            new() { MaxTokens = 1_000_001 },
            new() { TimeoutSeconds = 0 },
            new() { TimeoutSeconds = 3_601 },
            new() { MaxConcurrentRequests = 0 },
            new() { MaxConcurrentRequests = 65 },
        ];

        foreach (var options in invalidOptions)
        {
            var act = () => OpenAiLlmOptionsValidator.Validate(options);
            act.Should().Throw<InvalidOperationException>();
        }
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-0.1f)]
    [InlineData(2.1f)]
    public void ValidatorRejectsInvalidTemperature(float temperature)
    {
        var options = new OpenAiLlmOptions { Temperature = temperature };

        var act = () => OpenAiLlmOptionsValidator.Validate(options);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OpenAi:Temperature*finite value between 0 and 2*");
    }

    [Fact]
    public void ValidatorRejectsInvalidSamplingValues()
    {
        OpenAiSamplingOptions[] invalidSampling =
        [
            new() { TopP = float.NaN },
            new() { TopP = 1.01f },
            new() { TopK = 0 },
            new() { TopK = 1_000_001 },
            new() { MinP = -0.01f },
            new() { MinP = float.PositiveInfinity },
            new() { PresencePenalty = -2.01f },
            new() { PresencePenalty = 2.01f },
            new() { RepetitionPenalty = 0 },
            new() { RepetitionPenalty = 2.01f },
        ];

        foreach (var sampling in invalidSampling)
        {
            var options = new OpenAiLlmOptions { Sampling = sampling };
            var act = () => OpenAiLlmOptionsValidator.Validate(options);
            act.Should().Throw<InvalidOperationException>();
        }
    }
}
