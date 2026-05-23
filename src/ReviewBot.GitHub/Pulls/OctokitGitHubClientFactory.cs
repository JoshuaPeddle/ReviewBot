using Octokit;

namespace ReviewBot.GitHub.Pulls;

public sealed class OctokitGitHubClientFactory : IGitHubClientFactory
{
    private static readonly ProductHeaderValue ProductHeader = new("ReviewBot");

    public IGitHubClient CreateForInstallation(string installationToken)
    {
        if (string.IsNullOrWhiteSpace(installationToken))
        {
            throw new ArgumentException("GitHub installation token must be provided.", nameof(installationToken));
        }

        return new GitHubClient(ProductHeader)
        {
            Credentials = new Credentials(installationToken),
        };
    }
}
