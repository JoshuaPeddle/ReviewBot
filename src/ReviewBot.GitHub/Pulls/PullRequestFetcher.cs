using Microsoft.Extensions.Logging;
using Octokit;
using ReviewBot.Core.Diff;
using ReviewBot.Core.Domain;

namespace ReviewBot.GitHub.Pulls;

public sealed class PullRequestFetcher : IPullRequestFetcher
{
    private const int PageSize = 30;

    private readonly IGitHubClientFactory clientFactory;
    private readonly int maxFiles;
    private readonly TimeProvider clock;
    private readonly ILogger<PullRequestFetcher> logger;

    public PullRequestFetcher(
        IGitHubClientFactory clientFactory,
        TimeProvider? clock = null,
        ILogger<PullRequestFetcher>? logger = null)
        : this(clientFactory, ReviewConfig.Default.Review.MaxFiles, clock, logger)
    {
    }

    internal PullRequestFetcher(
        IGitHubClientFactory clientFactory,
        int maxFiles,
        TimeProvider? clock = null,
        ILogger<PullRequestFetcher>? logger = null)
    {
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        this.maxFiles = maxFiles > 0
            ? maxFiles
            : throw new ArgumentOutOfRangeException(nameof(maxFiles), maxFiles, "Max files must be positive.");
        this.clock = clock ?? TimeProvider.System;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PullRequestFetcher>.Instance;
    }

    public async Task<PullRequestSnapshot> FetchAsync(
        string owner,
        string repo,
        int prNumber,
        string installationToken,
        CancellationToken ct)
    {
        return await FetchAsync(owner, repo, prNumber, installationToken, maxFiles, ct)
            .ConfigureAwait(false);
    }

    public async Task<PullRequestSnapshot> FetchAsync(
        string owner,
        string repo,
        int prNumber,
        string installationToken,
        int maxFiles,
        CancellationToken ct)
    {
        ValidateInputs(owner, repo, prNumber, installationToken);
        if (maxFiles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFiles), maxFiles, "Max files must be positive.");
        }

        ct.ThrowIfCancellationRequested();

        var client = clientFactory.CreateForInstallation(installationToken);
        var pullRequest = await OctokitRateLimitRetry
            .ExecuteAsync(
                () => client.PullRequest.Get(owner, repo, prNumber),
                logger,
                clock,
                ct)
            .ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var files = await FetchFilesAsync(client, owner, repo, prNumber, maxFiles, ct).ConfigureAwait(false);

        return new PullRequestSnapshot(
            pullRequest.Title ?? string.Empty,
            pullRequest.Body ?? string.Empty,
            RequireSha(pullRequest.Base, "base"),
            RequireSha(pullRequest.Head, "head"),
            files);
    }

    private async Task<IReadOnlyList<FileChange>> FetchFilesAsync(
        IGitHubClient client,
        string owner,
        string repo,
        int prNumber,
        int maxFiles,
        CancellationToken ct)
    {
        var files = new List<FileChange>();
        var consideredFiles = 0;
        var page = 1;

        while (consideredFiles < maxFiles)
        {
            ct.ThrowIfCancellationRequested();

            var remaining = maxFiles - consideredFiles;
            var pageSize = Math.Min(PageSize, remaining);
            var pageFiles = await OctokitRateLimitRetry
                .ExecuteAsync(
                    () => client.PullRequest.Files(
                        owner,
                        repo,
                        prNumber,
                        new ApiOptions
                        {
                            StartPage = page,
                            PageCount = 1,
                            PageSize = pageSize,
                        }),
                    logger,
                    clock,
                    ct)
                .ConfigureAwait(false);

            if (pageFiles.Count == 0)
            {
                break;
            }

            foreach (var file in pageFiles.Take(remaining))
            {
                consideredFiles++;
                var patch = file.Patch ?? string.Empty;
                var commentableLines = UnifiedDiffParser.GetCommentableLines(patch);

                if (file.Patch is null && commentableLines.Count == 0)
                {
                    continue;
                }

                files.Add(new FileChange(
                    file.FileName ?? string.Empty,
                    patch,
                    commentableLines,
                    file.Additions,
                    file.Deletions,
                    MapStatus(file.Status)));
            }

            if (pageFiles.Count < pageSize)
            {
                break;
            }

            page++;
        }

        return files;
    }

    private static void ValidateInputs(string owner, string repo, int prNumber, string installationToken)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new ArgumentException("Repository owner must be provided.", nameof(owner));
        }

        if (string.IsNullOrWhiteSpace(repo))
        {
            throw new ArgumentException("Repository name must be provided.", nameof(repo));
        }

        if (prNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(prNumber), prNumber, "Pull request number must be positive.");
        }

        if (string.IsNullOrWhiteSpace(installationToken))
        {
            throw new ArgumentException("GitHub installation token must be provided.", nameof(installationToken));
        }
    }

    private static string RequireSha(GitReference? reference, string side) =>
        string.IsNullOrWhiteSpace(reference?.Sha)
            ? throw new InvalidOperationException($"GitHub pull request response is missing {side} SHA.")
            : reference.Sha;

    private static FileChangeStatus MapStatus(string? status) =>
        status?.ToLowerInvariant() switch
        {
            "added" => FileChangeStatus.Added,
            "modified" => FileChangeStatus.Modified,
            "removed" => FileChangeStatus.Removed,
            "renamed" => FileChangeStatus.Renamed,
            "copied" => FileChangeStatus.Copied,
            _ => throw new InvalidOperationException($"Unsupported GitHub file status '{status}'."),
        };
}
