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
    int? MaxOutputTokens = null,
    // True when this is an incremental re-review: the files below are only those
    // changed since the previous review, not the whole PR. Lets the prompt tell
    // the model to scope its summary to the update.
    bool IsIncrementalUpdate = false);

public sealed record RepositoryContextSnippet(
    string Path,
    int StartLine,
    int EndLine,
    string Content);
