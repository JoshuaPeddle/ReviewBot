using ReviewBot.Core.Domain;

namespace ReviewBot.GitHub.Pulls;

public sealed record PullRequestSnapshot(
    string Title,
    string Body,
    string BaseSha,
    string HeadSha,
    IReadOnlyList<FileChange> Files,
    string HeadCloneUrl = "");
