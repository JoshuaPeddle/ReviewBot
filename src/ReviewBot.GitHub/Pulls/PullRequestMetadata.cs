namespace ReviewBot.GitHub.Pulls;

public sealed record PullRequestMetadata(
    string Title,
    string Body,
    string BaseSha,
    string HeadSha,
    string HeadCloneUrl = "");
