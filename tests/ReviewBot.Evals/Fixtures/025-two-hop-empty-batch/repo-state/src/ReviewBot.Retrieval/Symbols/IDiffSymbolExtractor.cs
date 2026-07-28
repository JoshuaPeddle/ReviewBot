using ReviewBot.Core.Domain;

namespace ReviewBot.Retrieval.Symbols;

public interface IDiffSymbolExtractor
{
    IReadOnlyList<FileDiffSymbols> Extract(IReadOnlyList<FileChange> files);
}
