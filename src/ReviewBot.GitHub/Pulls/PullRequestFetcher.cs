using System.Text;
using Microsoft.Extensions.Logging;
using Octokit;
using ReviewBot.Core.Diff;
using ReviewBot.Core.Domain;

namespace ReviewBot.GitHub.Pulls;

public sealed class PullRequestFetcher : IPullRequestFetcher
{
    private const int PageSize = 30;
    private const int MaxParallelContentFetches = 3;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

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

        var files = await FetchFilesCappedAsync(client, owner, repo, prNumber, maxFiles, ct).ConfigureAwait(false);

        return new PullRequestSnapshot(
            pullRequest.Title ?? string.Empty,
            pullRequest.Body ?? string.Empty,
            RequireSha(pullRequest.Base, "base"),
            RequireSha(pullRequest.Head, "head"),
            files,
            GetHeadCloneUrl(owner, repo, pullRequest));
    }

    public async Task<PullRequestMetadata> FetchMetadataAsync(
        string owner,
        string repo,
        int prNumber,
        string installationToken,
        CancellationToken ct)
    {
        ValidateInputs(owner, repo, prNumber, installationToken);
        ct.ThrowIfCancellationRequested();

        var client = clientFactory.CreateForInstallation(installationToken);
        var pullRequest = await OctokitRateLimitRetry
            .ExecuteAsync(
                () => client.PullRequest.Get(owner, repo, prNumber),
                logger,
                clock,
                ct)
            .ConfigureAwait(false);

        return new PullRequestMetadata(
            pullRequest.Title ?? string.Empty,
            pullRequest.Body ?? string.Empty,
            RequireSha(pullRequest.Base, "base"),
            RequireSha(pullRequest.Head, "head"),
            GetHeadCloneUrl(owner, repo, pullRequest));
    }

    public async Task<IReadOnlyList<FileChange>> FetchFilesAsync(
        string owner,
        string repo,
        int prNumber,
        string installationToken,
        int maxFiles,
        IReadOnlySet<string>? pathAllowlist,
        CancellationToken ct)
    {
        ValidateInputs(owner, repo, prNumber, installationToken);
        if (maxFiles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFiles), maxFiles, "Max files must be positive.");
        }

        ct.ThrowIfCancellationRequested();

        var client = clientFactory.CreateForInstallation(installationToken);

        if (pathAllowlist is null)
        {
            return await FetchFilesCappedAsync(client, owner, repo, prNumber, maxFiles, ct).ConfigureAwait(false);
        }

        return await FetchFilesWithAllowlistAsync(client, owner, repo, prNumber, maxFiles, pathAllowlist, ct)
            .ConfigureAwait(false);
    }

    public async Task<ChangedFilesResult> GetChangedFilesSinceAsync(
        string owner,
        string repo,
        string baseSha,
        string headSha,
        string installationToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Repository owner must be provided.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository name must be provided.", nameof(repo));
        if (string.IsNullOrWhiteSpace(baseSha))
            throw new ArgumentException("Base SHA must be provided.", nameof(baseSha));
        if (string.IsNullOrWhiteSpace(headSha))
            throw new ArgumentException("Head SHA must be provided.", nameof(headSha));
        if (string.IsNullOrWhiteSpace(installationToken))
            throw new ArgumentException("GitHub installation token must be provided.", nameof(installationToken));

        ct.ThrowIfCancellationRequested();

        var client = clientFactory.CreateForInstallation(installationToken);
        var compareResult = await OctokitRateLimitRetry
            .ExecuteAsync(
                () => client.Repository.Commit.Compare(owner, repo, baseSha, headSha),
                logger,
                clock,
                ct)
            .ConfigureAwait(false);

        var paths = compareResult.Files
            .SelectMany(f => new[] { f.Filename, f.PreviousFileName })
            .Where(f => !string.IsNullOrEmpty(f))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // GitHub caps compare results at 300 files; at or above the cap the result is incomplete.
        const int GitHubCompareFilesCap = 300;
        return new ChangedFilesResult(paths, compareResult.Files.Count < GitHubCompareFilesCap);
    }

    public async Task<IReadOnlyList<(string Path, string Content)>> GetFileContentsAsync(
        string owner,
        string repo,
        IReadOnlyList<ContextRequest> requests,
        string sha,
        int maxBytes,
        string installationToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Repository owner must be provided.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository name must be provided.", nameof(repo));
        ArgumentNullException.ThrowIfNull(requests);
        if (string.IsNullOrWhiteSpace(sha))
            throw new ArgumentException("Content ref SHA must be provided.", nameof(sha));
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "Max bytes must be positive.");
        if (string.IsNullOrWhiteSpace(installationToken))
            throw new ArgumentException("GitHub installation token must be provided.", nameof(installationToken));

        ct.ThrowIfCancellationRequested();
        if (requests.Count == 0)
        {
            return Array.Empty<(string Path, string Content)>();
        }

        var client = clientFactory.CreateForInstallation(installationToken);
        using var semaphore = new SemaphoreSlim(MaxParallelContentFetches, MaxParallelContentFetches);

        var tasks = requests.Select(async (request, index) =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var content = await TryFetchTextFileAsync(client, owner, repo, request.Path, sha, maxBytes, ct)
                    .ConfigureAwait(false);
                return new FetchedContent(index, request.Path, content);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        var fetched = await Task.WhenAll(tasks).ConfigureAwait(false);
        return fetched
            .Where(item => item.Content is not null)
            .OrderBy(item => item.Index)
            .Select(item => (item.Path, item.Content!))
            .ToArray();
    }

    private async Task<IReadOnlyList<FileChange>> FetchFilesCappedAsync(
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

                if (string.IsNullOrEmpty(file.Patch) && commentableLines.Count == 0)
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

    private async Task<IReadOnlyList<FileChange>> FetchFilesWithAllowlistAsync(
        IGitHubClient client,
        string owner,
        string repo,
        int prNumber,
        int maxFiles,
        IReadOnlySet<string> pathAllowlist,
        CancellationToken ct)
    {
        var files = new List<FileChange>();
        var foundPaths = new HashSet<string>(StringComparer.Ordinal);
        var page = 1;

        while (foundPaths.Count < pathAllowlist.Count)
        {
            ct.ThrowIfCancellationRequested();

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
                            PageSize = PageSize,
                        }),
                    logger,
                    clock,
                    ct)
                .ConfigureAwait(false);

            if (pageFiles.Count == 0)
            {
                break;
            }

            foreach (var file in pageFiles)
            {
                var path = file.FileName ?? string.Empty;
                if (!pathAllowlist.Contains(path))
                {
                    continue;
                }

                foundPaths.Add(path);

                var patch = file.Patch ?? string.Empty;
                var commentableLines = UnifiedDiffParser.GetCommentableLines(patch);

                if (string.IsNullOrEmpty(file.Patch) && commentableLines.Count == 0)
                {
                    continue;
                }

                files.Add(new FileChange(
                    path,
                    patch,
                    commentableLines,
                    file.Additions,
                    file.Deletions,
                    MapStatus(file.Status)));
            }

            if (pageFiles.Count < PageSize)
            {
                break;
            }

            page++;
        }

        return files.Count > maxFiles ? files.Take(maxFiles).ToArray() : files;
    }

    private async Task<string?> TryFetchTextFileAsync(
        IGitHubClient client,
        string owner,
        string repo,
        string path,
        string sha,
        int maxBytes,
        CancellationToken ct)
    {
        try
        {
            var contents = await OctokitRateLimitRetry
                .ExecuteAsync(
                    () => client.Repository.Content.GetAllContentsByRef(owner, repo, path, sha),
                    logger,
                    clock,
                    ct)
                .ConfigureAwait(false);
            var file = contents.Count == 1 ? contents[0] : null;
            if (file is null || file.Type != ContentType.File || file.EncodedContent is null)
            {
                return null;
            }

            if (file.Size > maxBytes)
            {
                return null;
            }

            var bytes = Convert.FromBase64String(file.EncodedContent);
            if (bytes.Length > maxBytes || bytes.Contains((byte)0))
            {
                return null;
            }

            return StrictUtf8.GetString(bytes);
        }
        catch (NotFoundException)
        {
            return null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
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

    private static string GetHeadCloneUrl(string owner, string repo, PullRequest pullRequest)
    {
        var cloneUrl = pullRequest.Head.Repository?.CloneUrl;
        if (!string.IsNullOrWhiteSpace(cloneUrl))
        {
            return cloneUrl;
        }

        return $"https://github.com/{owner}/{repo}.git";
    }

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

    private sealed record FetchedContent(int Index, string Path, string? Content);
}
