namespace ReviewBot.Api.Webhooks;

public sealed record WebhookOptions
{
    public const string SectionName = "Webhook";

    public string Secret { get; set; } = "";

    public string BotSlug { get; set; } = "";
}
