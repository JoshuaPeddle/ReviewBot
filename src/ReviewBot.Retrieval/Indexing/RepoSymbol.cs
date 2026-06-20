namespace ReviewBot.Retrieval.Indexing;

public enum RepoSymbolKind
{
    Type = 0,
    Method = 1,
    Field = 2,
    Import = 3
}

public enum RepoSymbolRole
{
    Definition = 0,
    Usage = 1
}

public sealed record RepoIndexKey(
    string Owner,
    string Repo,
    string Sha);

public sealed record RepoIndexRequest(
    string Owner,
    string Repo,
    string Sha,
    string RepositoryRoot,
    IReadOnlyList<string>? Ignore = null);

public sealed record RepoSymbol(
    string Name,
    RepoSymbolKind Kind,
    RepoSymbolRole Role,
    string Path,
    int Line,
    string? Signature,
    string? Body = null,
    int? BodyStartLine = null,
    int? BodyEndLine = null);
