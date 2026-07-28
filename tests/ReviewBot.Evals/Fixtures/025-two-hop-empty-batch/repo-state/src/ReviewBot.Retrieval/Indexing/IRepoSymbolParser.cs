namespace ReviewBot.Retrieval.Indexing;

public interface IRepoSymbolParser
{
    bool CanParse(string path);

    IReadOnlyList<RepoSymbol> Parse(string path, string content);
}
