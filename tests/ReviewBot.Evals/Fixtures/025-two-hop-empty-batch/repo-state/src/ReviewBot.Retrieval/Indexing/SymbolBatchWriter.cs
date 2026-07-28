namespace ReviewBot.Retrieval.Indexing;

/// <summary>
/// Writes a batch of parsed symbols into the index for one commit.
/// </summary>
internal sealed class SymbolBatchWriter
{
    /// <summary>
    /// Validates and persists a batch. Callers are expected to have something to write.
    /// </summary>
    public void WriteBatch(IReadOnlyList<SymbolRow> rows, string sha)
    {
        SymbolBatchValidator.EnsureNotEmpty(rows, sha);

        foreach (var row in rows)
        {
            Persist(row, sha);
        }
    }

    private static void Persist(SymbolRow row, string sha)
    {
        // Storage details omitted.
        _ = row;
        _ = sha;
    }
}
