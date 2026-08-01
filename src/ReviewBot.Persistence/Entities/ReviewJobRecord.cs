namespace ReviewBot.Persistence.Entities;

public sealed class ReviewJobRecord
{
    public string DeliveryId { get; set; } = string.Empty;
    public long InstallationId { get; set; }
    public string Owner { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
    public int PrNumber { get; set; }
    public string? HeadSha { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public string? LeaseToken { get; set; }
    public string? LastError { get; set; }
}
