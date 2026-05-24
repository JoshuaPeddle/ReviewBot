namespace ReviewBot.Core.Domain;

public sealed record ReviewRequest(
    string PrTitle,
    string PrBody,
    string BaseSha,
    string HeadSha,
    IReadOnlyList<FileChange> Files,
    ReviewConfig Config,
    GroundingContext? Grounding = null,
    IReadOnlyDictionary<string, string>? FullFileContents = null);
