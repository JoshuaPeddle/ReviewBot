namespace ReviewBot.Core.Sessions;

public sealed class SessionService
{
    private readonly SessionCache cache;

    public SessionService(SessionCache cache) => this.cache = cache;

    public Task<Session[]> ResolveManyAsync(IEnumerable<string> ids, Func<string, Task<Session>> loader)
    {
        var tasks = ids.Select(id => cache.GetOrCreateAsync(id, () => loader(id)));
        return Task.WhenAll(tasks);
    }
}
