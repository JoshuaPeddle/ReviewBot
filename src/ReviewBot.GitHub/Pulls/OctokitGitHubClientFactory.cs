using Microsoft.Extensions.Options;
using Octokit;
using ReviewBot.GitHub.Auth;

namespace ReviewBot.GitHub.Pulls;

public sealed class OctokitGitHubClientFactory : IGitHubClientFactory
{
    private static readonly ProductHeaderValue ProductHeader = new("ReviewBot");

    private readonly Uri apiBaseUrl;

    public OctokitGitHubClientFactory()
        : this(Options.Create(new GitHubAppOptions()))
    {
    }

    public OctokitGitHubClientFactory(IOptions<GitHubAppOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        apiBaseUrl = NormalizeBaseUrl(options.Value.ApiBaseUrl);
    }

    public IGitHubClient CreateForInstallation(string installationToken)
    {
        if (string.IsNullOrWhiteSpace(installationToken))
        {
            throw new ArgumentException("GitHub installation token must be provided.", nameof(installationToken));
        }

        return new GitHubClient(new Connection(ProductHeader, apiBaseUrl))
        {
            Credentials = new Credentials(installationToken),
        };
    }

    private static Uri NormalizeBaseUrl(Uri apiBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(apiBaseUrl);

        var text = apiBaseUrl.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? apiBaseUrl.AbsoluteUri
            : $"{apiBaseUrl.AbsoluteUri}/";
        return new Uri(text, UriKind.Absolute);
    }
}
