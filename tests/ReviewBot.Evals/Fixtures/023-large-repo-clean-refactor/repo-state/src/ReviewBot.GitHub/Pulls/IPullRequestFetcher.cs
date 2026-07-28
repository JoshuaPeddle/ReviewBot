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

    Task<PullRequestMetadata> FetchMetadataAsync(
        string owner,
        string repo,
        int prNumber,
        string installationToken,
        CancellationToken ct);

    Task<IReadOnlyList<FileChange>> FetchFilesAsync(
        string owner,
        string repo,
        int prNumber,
        string installationToken,
        int maxFiles,
        IReadOnlySet<string>? pathAllowlist,
        CancellationToken ct);

    Task<ChangedFilesResult> GetChangedFilesSinceAsync(
        string owner,
        string repo,
        string baseSha,
        string headSha,
        string installationToken,
        CancellationToken ct);

    Task<IReadOnlyList<(string Path, string Content)>> GetFileContentsAsync(
        string owner,
        string repo,
        IReadOnlyList<ContextRequest> requests,
        string sha,
        int maxBytes,
        string installationToken,
        CancellationToken ct);
}
