namespace ReviewBot.Persistence.Entities;

public sealed class DeliveryRecord
{
    public string DeliveryId { get; set; } = string.Empty;

    public DateTimeOffset ProcessedAt { get; set; }
}
