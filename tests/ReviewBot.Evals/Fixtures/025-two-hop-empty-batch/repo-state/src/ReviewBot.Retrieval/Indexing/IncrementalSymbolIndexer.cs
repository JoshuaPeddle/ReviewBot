namespace ReviewBot.Retrieval.Indexing;

/// <summary>
/// Re-indexes only the paths that changed in a push, rather than the whole repository.
/// </summary>
internal sealed class IncrementalSymbolIndexer
{
    private readonly SymbolBatchWriter writer;

    public IncrementalSymbolIndexer(SymbolBatchWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        this.writer = writer;
    }

    /// <summary>
    /// Indexes the symbols belonging to the changed paths for this commit.
    /// </summary>
    public void IndexChangedPaths(
        IReadOnlyList<SymbolRow> parsedRows,
        IReadOnlySet<string> changedPaths,
        string sha)
    {
        ArgumentNullException.ThrowIfNull(parsedRows);
        ArgumentNullException.ThrowIfNull(changedPaths);

        var rows = parsedRows
            .Where(row => changedPaths.Contains(row.Path))
            .ToArray();

        this.writer.WriteBatch(rows, sha);
    }
}
