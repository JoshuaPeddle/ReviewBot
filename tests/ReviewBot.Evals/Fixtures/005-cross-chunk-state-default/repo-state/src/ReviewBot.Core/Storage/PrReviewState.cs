namespace ReviewBot.Core.Storage;

public sealed record PrReviewState(
    string Owner,
    string Repo,
    int PullRequestNumber,
    string? LastReviewedHeadSha);
