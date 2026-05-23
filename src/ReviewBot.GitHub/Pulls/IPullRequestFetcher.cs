using ReviewBot.Core.Domain;

namespace ReviewBot.GitHub.Pulls;

public interface IPullRequestFetcher
{
    Task<PullRequestSnapshot> FetchAsync(
        string owner,
        string repo,
        int prNumber,
        string installationToken,
        CancellationToken ct);

    Task<PullRequestSnapshot> FetchAsync(
        string owner,
        string repo,
        int prNumber,
        string installationToken,
        int maxFiles,
        CancellationToken ct);
}
