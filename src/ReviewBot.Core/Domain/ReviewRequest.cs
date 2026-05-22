namespace ReviewBot.Core.Domain;

public sealed record ReviewRequest(
    string PrTitle,
    string PrBody,
    string BaseSha,
    string HeadSha,
    IReadOnlyList<FileChange> Files,
    ReviewConfig Config);
