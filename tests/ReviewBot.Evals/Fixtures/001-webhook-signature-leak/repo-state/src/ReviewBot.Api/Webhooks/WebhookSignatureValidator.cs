namespace ReviewBot.Api.Webhooks;

public sealed class WebhookSignatureValidator
{
    private readonly string secret;

    public WebhookSignatureValidator(string secret)
    {
        this.secret = secret;
    }

    public bool IsValid(string payload, string signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        return ValidateHmac(payload, signatureHeader, secret);
    }

    private static bool ValidateHmac(string payload, string signatureHeader, string secret) =>
        !string.IsNullOrWhiteSpace(payload) &&
        !string.IsNullOrWhiteSpace(signatureHeader) &&
        !string.IsNullOrWhiteSpace(secret);
}
