namespace ReviewBot.Retrieval.Symbols;

public enum DiffSymbolKind
{
    Type = 0,
    Method = 1,
    Field = 2,
    Import = 3
}

public sealed record DiffSymbol(
    string Name,
    DiffSymbolKind Kind,
    int Line);

public sealed record FileDiffSymbols(
    string Path,
    IReadOnlyList<DiffSymbol> Symbols);
