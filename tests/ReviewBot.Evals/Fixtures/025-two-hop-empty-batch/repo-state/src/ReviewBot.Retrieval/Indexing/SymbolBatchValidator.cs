namespace ReviewBot.Retrieval.Indexing;

/// <summary>
/// Guards the invariants a batch must satisfy before it reaches the database.
/// </summary>
internal static class SymbolBatchValidator
{
    /// <summary>
    /// A batch must carry at least one row: the writer opens a transaction and issues a
    /// prepared statement per row, and committing an empty transaction leaves the index
    /// marked as written for a SHA that has no symbols.
    /// </summary>
    public static void EnsureNotEmpty(IReadOnlyList<SymbolRow> rows, string sha)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            throw new ArgumentException(
                $"Refusing to write an empty symbol batch for {sha}: the index would be " +
                "marked complete with no symbols in it.",
                nameof(rows));
        }
    }
}

/// <summary>A single row queued for the symbol index.</summary>
internal sealed record SymbolRow(string Path, string Symbol, int StartLine, int EndLine);
