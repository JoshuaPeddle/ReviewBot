using ReviewBot.Core.Sessions;

namespace ReviewBot.Api.Sessions;

public sealed class SessionReader
{
    private readonly ISessionCache cache;

    public SessionReader(ISessionCache cache)
    {
        this.cache = cache;
    }

    public Task<ReviewSession?> GetAsync(string userId, CancellationToken ct) =>
        cache.GetAsync(SessionCacheKeys.ForUser(userId), ct);
}
