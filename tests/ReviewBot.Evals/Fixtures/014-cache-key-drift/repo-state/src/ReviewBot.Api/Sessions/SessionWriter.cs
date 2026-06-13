using ReviewBot.Core.Sessions;

namespace ReviewBot.Api.Sessions;

public sealed class SessionWriter
{
    private readonly ISessionCache cache;

    public SessionWriter(ISessionCache cache)
    {
        this.cache = cache;
    }

    public async Task StoreAsync(string userId, ReviewSession session, CancellationToken ct)
    {
        var key = SessionCacheKeys.ForTenantUser(session.TenantId, userId);
        await cache.SetAsync(key, session, ct).ConfigureAwait(false);
    }
}
