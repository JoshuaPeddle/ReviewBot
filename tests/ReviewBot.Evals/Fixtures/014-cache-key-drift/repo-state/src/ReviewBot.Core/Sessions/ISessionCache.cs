namespace ReviewBot.Core.Sessions;

public interface ISessionCache
{
    Task<ReviewSession?> GetAsync(string key, CancellationToken ct);

    Task SetAsync(string key, ReviewSession session, CancellationToken ct);
}

public sealed record ReviewSession(string TenantId, string UserId, string PullRequestUrl, DateTimeOffset LastSeenUtc);
