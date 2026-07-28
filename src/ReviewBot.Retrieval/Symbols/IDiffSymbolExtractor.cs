using ReviewBot.Core.Domain;

namespace ReviewBot.Retrieval.Symbols;

public interface IDiffSymbolExtractor
{
    IReadOnlyList<FileDiffSymbols> Extract(IReadOnlyList<FileChange> files);

    /// <summary>
    /// Extracts symbols from plain source, for the second retrieval hop.
    /// </summary>
    /// <remarks>
    /// Defaults to none so an extractor for a language without transitive support stays
    /// valid; retrieval then simply behaves as it did before, at one hop.
    /// </remarks>
    IReadOnlyList<DiffSymbol> ExtractFromSource(string source) => [];
}
