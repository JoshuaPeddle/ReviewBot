namespace ReviewBot.Core.Domain;

public sealed record ReviewRequest(
    string PrTitle,
    string PrBody,
    string BaseSha,
    string HeadSha,
    IReadOnlyList<FileChange> Files,
    ReviewConfig Config,
    GroundingContext? Grounding = null,
    IReadOnlyDictionary<string, string>? FullFileContents = null,
    IReadOnlyList<RepositoryContextSnippet>? RepositoryContext = null);

public sealed record RepositoryContextSnippet(
    string Path,
    int StartLine,
    int EndLine,
    string Content);
