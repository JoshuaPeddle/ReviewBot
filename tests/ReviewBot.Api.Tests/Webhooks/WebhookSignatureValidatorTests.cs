using System.Text;
using FluentAssertions;
using ReviewBot.Api.Webhooks;

namespace ReviewBot.Api.Tests.Webhooks;

public class WebhookSignatureValidatorTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("Hello, World!");

    [Fact]
    public void KnownBodyAndSecretProducesKnownHmac()
    {
        const string signature = "sha256=e3cade22487dd0eafea819342fd29d1cfa30b55e9fd028d8c962eebac19e8a3c";

        var isValid = WebhookSignatureValidator.IsValid("top-secret", Body, signature);

        isValid.Should().BeTrue();
    }

    [Fact]
    public void WrongSecretReturnsFalse()
    {
        const string signature = "sha256=e3cade22487dd0eafea819342fd29d1cfa30b55e9fd028d8c962eebac19e8a3c";

        var isValid = WebhookSignatureValidator.IsValid("wrong-secret", Body, signature);

        isValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingHeaderReturnsFalse(string? signature)
    {
        var isValid = WebhookSignatureValidator.IsValid("top-secret", Body, signature);

        isValid.Should().BeFalse();
    }

    [Fact]
    public void WrongPrefixReturnsFalse()
    {
        const string signature = "sha1=e3cade22487dd0eafea819342fd29d1cfa30b55e9fd028d8c962eebac19e8a3c";

        var isValid = WebhookSignatureValidator.IsValid("top-secret", Body, signature);

        isValid.Should().BeFalse();
    }

    [Fact]
    public void TamperedBodyReturnsFalse()
    {
        const string signature = "sha256=e3cade22487dd0eafea819342fd29d1cfa30b55e9fd028d8c962eebac19e8a3c";
        var tamperedBody = Encoding.UTF8.GetBytes("Hello, World?");

        var isValid = WebhookSignatureValidator.IsValid("top-secret", tamperedBody, signature);

        isValid.Should().BeFalse();
    }

    [Fact]
    public void WrongSignatureLengthReturnsFalse()
    {
        const string signature = "sha256=e3cade22487dd0eafea819342fd29d1cfa30b55e9fd028d8c962eebac19e8a3";

        var isValid = WebhookSignatureValidator.IsValid("top-secret", Body, signature);

        isValid.Should().BeFalse();
    }

    [Fact]
    public void NonHexSignatureReturnsFalse()
    {
        const string signature = "sha256=zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz";

        var isValid = WebhookSignatureValidator.IsValid("top-secret", Body, signature);

        isValid.Should().BeFalse();
    }
}
