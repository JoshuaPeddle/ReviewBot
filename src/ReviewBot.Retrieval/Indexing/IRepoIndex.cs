namespace ReviewBot.Retrieval.Indexing;

public interface IRepoIndex
{
    Task IndexAsync(RepoIndexRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<RepoSymbol>> FindAsync(
        RepoIndexKey key,
        string name,
        RepoSymbolKind? kind = null,
        CancellationToken ct = default);

    Task<int> DeleteUnusedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
