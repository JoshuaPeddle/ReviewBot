using System.Collections.Concurrent;

namespace ReviewBot.Retrieval.Indexing;

public interface IRepoIndexFactory
{
    IRepoIndex Create(string indexCacheDir);

    IReadOnlyList<string> GetKnownCacheDirectories();
}

public sealed class SqliteRepoIndexFactory(
    IEnumerable<IRepoSymbolParser> parsers,
    TimeProvider clock) : IRepoIndexFactory
{
    private readonly IReadOnlyList<IRepoSymbolParser> parsers = parsers.ToArray();
    private readonly ConcurrentDictionary<string, byte> knownCacheDirectories = new(StringComparer.Ordinal);

    public IRepoIndex Create(string indexCacheDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexCacheDir);

        var fullPath = Path.GetFullPath(indexCacheDir);
        knownCacheDirectories.TryAdd(fullPath, 0);
        return SqliteRepoIndex.CreateForCacheDirectory(fullPath, parsers, clock);
    }

    public IReadOnlyList<string> GetKnownCacheDirectories() =>
        knownCacheDirectories.Keys.Order(StringComparer.Ordinal).ToArray();
}
