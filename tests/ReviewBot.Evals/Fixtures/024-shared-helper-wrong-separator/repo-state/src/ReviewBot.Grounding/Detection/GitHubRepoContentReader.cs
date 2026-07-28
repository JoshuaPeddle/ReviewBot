using System.Text;
using Octokit;
using ReviewBot.GitHub.Pulls;

namespace ReviewBot.Grounding.Detection;

public sealed class GitHubRepoContentReader : IRepoContentReader
{
    private readonly IGitHubClient client;
    private readonly string owner;
    private readonly string repo;

    public GitHubRepoContentReader(
        IGitHubClientFactory factory,
        string owner,
        string repo,
        string installationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Owner required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo)) throw new ArgumentException("Repo required.", nameof(repo));
        if (string.IsNullOrWhiteSpace(installationToken)) throw new ArgumentException("Token required.", nameof(installationToken));

        this.owner = owner;
        this.repo = repo;
        client = factory.CreateForInstallation(installationToken);
    }

    public async Task<IReadOnlyList<string>> ListRootFilesAsync(string sha, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tree = await client.Git.Tree.Get(owner, repo, sha).ConfigureAwait(false);
        return tree.Tree
            .Where(item => item.Type == TreeType.Blob)
            .Select(item => item.Path)
            .ToArray();
    }

    public async Task<string?> TryReadFileAsync(string path, string sha, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var contents = await client.Repository.Content
                .GetAllContentsByRef(owner, repo, path, sha)
                .ConfigureAwait(false);
            var file = contents.Count == 1 ? contents[0] : null;
            if (file?.EncodedContent is null)
            {
                return null;
            }

            return Encoding.UTF8.GetString(Convert.FromBase64String(file.EncodedContent));
        }
        catch (NotFoundException)
        {
            return null;
        }
    }
}
