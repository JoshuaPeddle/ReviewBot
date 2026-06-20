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
    IReadOnlyList<RepositoryContextSnippet>? RepositoryContext = null,
    int? ChunkIndex = null,
    int? TotalChunks = null,
    // Output-token allowance for the LLM call, derived from the prompt budget's
    // response reserve so that prompt + output is guaranteed to fit the model
    // context window. Null falls back to the provider's configured default.
    int? MaxOutputTokens = null);

public sealed record RepositoryContextSnippet(
    string Path,
    int StartLine,
    int EndLine,
    string Content);
