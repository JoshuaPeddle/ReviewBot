using Octokit;

namespace ReviewBot.GitHub.Pulls;

public interface IGitHubClientFactory
{
    IGitHubClient CreateForInstallation(string installationToken);
}
