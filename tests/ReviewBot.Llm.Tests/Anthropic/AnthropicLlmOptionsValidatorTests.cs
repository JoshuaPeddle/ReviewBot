using FluentAssertions;
using ReviewBot.Llm.Anthropic;

namespace ReviewBot.Llm.Tests.Anthropic;

public sealed class AnthropicLlmOptionsValidatorTests
{
    [Fact]
    public void RegistrationAllowsOmittedApiKey()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        var act = () => services.AddAnthropicReviewLlm(_ => { });

        act.Should().NotThrow();
    }

    [Fact]
    public void RegistrationRejectsBlankModelEagerly()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        var act = () => services.AddAnthropicReviewLlm(options => options.ModelName = " ");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Anthropic:ModelName*");
    }

    [Fact]
    public void ValidatorRejectsInvalidOperationalIntegerValues()
    {
        AnthropicLlmOptions[] invalidOptions =
        [
            new() { MaxTokens = 0 },
            new() { MaxTokens = 1_000_001 },
            new() { TokenCountingHeuristicThresholdTokens = 0 },
            new() { TokenCountingHeuristicThresholdTokens = 1_000_001 },
            new() { MaxConcurrentRequests = 0 },
            new() { MaxConcurrentRequests = 65 },
        ];

        foreach (var options in invalidOptions)
        {
            var act = () => AnthropicLlmOptionsValidator.Validate(options);
            act.Should().Throw<InvalidOperationException>();
        }
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void ValidatorRejectsInvalidTemperature(double value)
    {
        var options = new AnthropicLlmOptions { Temperature = (decimal)value };

        var act = () => AnthropicLlmOptionsValidator.Validate(options);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Anthropic:Temperature*between 0 and 1*");
    }
}
