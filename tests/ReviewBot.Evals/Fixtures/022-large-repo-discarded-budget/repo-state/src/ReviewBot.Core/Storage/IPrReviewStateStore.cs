namespace ReviewBot.Core.Storage;

public interface IPrReviewStateStore
{
    Task<string?> GetLastShaAsync(long installationId, string repoFullName, int pullNumber, CancellationToken ct);
    Task SetLastShaAsync(long installationId, string repoFullName, int pullNumber, string sha, CancellationToken ct);
}
