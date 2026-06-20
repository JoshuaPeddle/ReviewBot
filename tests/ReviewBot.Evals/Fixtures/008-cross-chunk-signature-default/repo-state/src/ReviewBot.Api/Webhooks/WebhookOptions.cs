namespace ReviewBot.Api.Webhooks;

public sealed record WebhookOptions
{
    public bool RequireSignature { get; init; } = false;
}
