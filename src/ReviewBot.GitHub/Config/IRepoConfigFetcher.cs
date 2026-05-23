using ReviewBot.Core.Domain;

namespace ReviewBot.GitHub.Config;

public interface IRepoConfigFetcher
{
    Task<ReviewConfig> FetchAsync(
        string owner,
        string repo,
        string sha,
        string installationToken,
        CancellationToken ct);
}
