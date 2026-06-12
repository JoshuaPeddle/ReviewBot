namespace ReviewBot.Core.Sessions;

public sealed class SessionCache
{
    private readonly Dictionary<string, Session> entries = new();

    public async Task<Session> GetOrCreateAsync(string id, Func<Task<Session>> factory)
    {
        if (entries.TryGetValue(id, out var existing)) return existing;
        var created = await factory();
        entries[id] = created;
        return created;
    }
}

public sealed record Session(string Id, DateTimeOffset CreatedAt);
