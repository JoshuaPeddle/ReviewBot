namespace ReviewBot.Persistence.Entities;

public sealed class PrReviewStateRecord
{
    public long InstallationId { get; set; }
    public string RepoFullName { get; set; } = string.Empty;
    public int PullNumber { get; set; }
    public string LastSha { get; set; } = string.Empty;
    public DateTimeOffset ReviewedAt { get; set; }
}
