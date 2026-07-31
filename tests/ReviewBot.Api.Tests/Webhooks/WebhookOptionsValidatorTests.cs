using FluentAssertions;
using ReviewBot.Api.Webhooks;

namespace ReviewBot.Api.Tests.Webhooks;

public class WebhookOptionsValidatorTests
{
    private readonly WebhookOptionsValidator validator = new();

    [Fact]
    public void RejectsMissingBotSlug()
    {
        var result = validator.Validate(null, new WebhookOptions { Secret = "secret" });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(failure => failure.Contains("BotSlug", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptsCompleteWebhookConfiguration()
    {
        var result = validator.Validate(
            null,
            new WebhookOptions { Secret = "secret", BotSlug = "reviewbot[bot]" });

        result.Succeeded.Should().BeTrue();
    }
}
