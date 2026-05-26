namespace ReviewBot.Core.Jobs;

public sealed record ReviewJob(
    string DeliveryId,
    long InstallationId,
    string Owner,
    string Repo,
    int PullRequestNumber,
    string HeadSha);
