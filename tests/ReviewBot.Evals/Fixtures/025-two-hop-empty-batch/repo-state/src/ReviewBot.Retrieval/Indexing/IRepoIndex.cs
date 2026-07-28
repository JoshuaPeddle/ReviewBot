namespace ReviewBot.Retrieval.Indexing;

public interface IRepoIndex
{
    Task IndexAsync(RepoIndexRequest request, CancellationToken ct = default);

    Task IndexChangesAsync(
        RepoIndexRequest request,
        RepoIndexKey baseKey,
        IReadOnlyCollection<string> changedPaths,
        CancellationToken ct = default);

    Task<bool> IsIndexedAsync(RepoIndexKey key, CancellationToken ct = default);

    Task<IReadOnlyList<RepoSymbol>> FindAsync(
        RepoIndexKey key,
        string name,
        RepoSymbolKind? kind = null,
        CancellationToken ct = default);

    Task<int> DeleteUnusedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
