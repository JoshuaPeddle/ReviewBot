using System.Collections.Concurrent;
using Octokit;
using ReviewBot.Core.Domain;
using ReviewBot.GitHub.Auth;
using ReviewBot.GitHub.Config;
using ReviewBot.GitHub.Pulls;

namespace ReviewBot.Evals;

/// <summary>
/// Everything one fixture needs the faked GitHub surface to answer, plus the slot its
/// finished <see cref="ReviewResult"/> lands in.
/// </summary>
internal sealed class WorkerEvalFixtureState(
    EvalFixture fixture,
    ReviewConfig config,
    PullRequestSnapshot snapshot,
    string? repoStateDirectory)
{
    public EvalFixture Fixture { get; } = fixture;

    public ReviewConfig Config { get; } = config;

    public PullRequestSnapshot Snapshot { get; } = snapshot;

    public string? RepoStateDirectory { get; } = repoStateDirectory;

    public TaskCompletionSource<ReviewResult> Posted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// The GitHub side of the world for a worker-driven eval run, keyed by PR number so every
/// fixture can be in flight at once against one hosted app.
/// </summary>
/// <remarks>
/// This replaces the four GitHub seams the worker touches rather than stubbing HTTP, which is
/// the same approach <c>OllamaReviewE2eTests</c> takes. The point of the exercise is that
/// everything *between* those seams — budgeting, chunk planning, filtering, self-critique,
/// verification, output caps, review-event selection — is the real
/// <c>ReviewWorker.ProcessAsync</c> and not a reimplementation of it.
/// </remarks>
internal sealed class WorkerEvalGitHub : IInstallationTokenProvider, IRepoConfigFetcher, IPullRequestFetcher, IReviewPoster
{
    public const string Token = "worker-eval-token";

    private readonly ConcurrentDictionary<int, WorkerEvalFixtureState> states = new();

    public void Register(int prNumber, WorkerEvalFixtureState state) => states[prNumber] = state;

    private WorkerEvalFixtureState Get(int prNumber) =>
        states.TryGetValue(prNumber, out var state)
            ? state
            : throw new InvalidOperationException($"No eval fixture registered for PR #{prNumber}.");

    public Task<InstallationToken> GetTokenAsync(long installationId, CancellationToken ct) =>
        Task.FromResult(new InstallationToken(Token, DateTimeOffset.UtcNow.AddHours(1)));

    public Task<ReviewConfig> FetchAsync(
        string owner, string repo, string sha, string installationToken, CancellationToken ct)
    {
        // The config is per-fixture but this seam is not keyed by PR number, so every fixture
        // in a run must share one config. The runner enforces that by building a single
        // config for the whole run rather than per fixture.
        var first = states.Values.FirstOrDefault()
            ?? throw new InvalidOperationException("No eval fixtures registered.");
        return Task.FromResult(first.Config);
    }

    public Task<PullRequestSnapshot> FetchAsync(
        string owner, string repo, int prNumber, string installationToken, CancellationToken ct) =>
        Task.FromResult(Get(prNumber).Snapshot);

    public Task<PullRequestSnapshot> FetchAsync(
        string owner, string repo, int prNumber, string installationToken, int maxFiles, CancellationToken ct) =>
        Task.FromResult(Get(prNumber).Snapshot);

    public Task<PullRequestMetadata> FetchMetadataAsync(
        string owner, string repo, int prNumber, string installationToken, CancellationToken ct)
    {
        var snapshot = Get(prNumber).Snapshot;
        return Task.FromResult(new PullRequestMetadata(
            snapshot.Title, snapshot.Body, snapshot.BaseSha, snapshot.HeadSha, snapshot.HeadCloneUrl));
    }

    public Task<IReadOnlyList<FileChange>> FetchFilesAsync(
        string owner,
        string repo,
        int prNumber,
        string installationToken,
        int maxFiles,
        IReadOnlySet<string>? pathAllowlist,
        CancellationToken ct)
    {
        var files = Get(prNumber).Snapshot.Files;
        if (pathAllowlist is not null)
        {
            files = files.Where(file => pathAllowlist.Contains(file.Path)).ToArray();
        }

        return Task.FromResult(files);
    }

    // Each fixture is reviewed exactly once against a fresh database, so nothing ever asks
    // what changed since a previous head.
    public Task<ChangedFilesResult> GetChangedFilesSinceAsync(
        string owner, string repo, string baseSha, string headSha, string installationToken, CancellationToken ct) =>
        throw new NotSupportedException("Worker eval runs review each fixture once; there is no incremental delta.");

    /// <summary>
    /// Serves file bodies out of the fixture's <c>repo-state</c> directory, which is the head
    /// state of the fixture repo — exactly what production fetches from GitHub. This is the
    /// path both full-file context and agentic context fetches take.
    /// </summary>
    public Task<IReadOnlyList<(string Path, string Content)>> GetFileContentsAsync(
        string owner,
        string repo,
        IReadOnlyList<ContextRequest> requests,
        string sha,
        int maxBytes,
        string installationToken,
        CancellationToken ct)
    {
        var prNumber = WorkerEvalIdentity.PrNumberFromSha(sha);
        var repoState = Get(prNumber).RepoStateDirectory;
        var contents = new List<(string Path, string Content)>();
        if (repoState is null)
        {
            return Task.FromResult<IReadOnlyList<(string, string)>>(contents);
        }

        foreach (var request in requests)
        {
            var full = Path.Combine(repoState, request.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                continue;
            }

            var text = File.ReadAllText(full);
            if (text.Length > maxBytes)
            {
                text = text[..maxBytes];
            }

            contents.Add((request.Path, text));
        }

        return Task.FromResult<IReadOnlyList<(string, string)>>(contents);
    }

    public Task PostAsync(
        string owner,
        string repo,
        int prNumber,
        string commitSha,
        ReviewResult result,
        IReadOnlyList<FileChange> files,
        string installationToken,
        CancellationToken ct,
        PullRequestReviewEvent reviewEvent = PullRequestReviewEvent.Comment)
    {
        Get(prNumber).Posted.TrySetResult(result);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The head SHA carries the PR number so seams that receive a SHA but not a PR number
/// (<see cref="WorkerEvalGitHub.GetFileContentsAsync"/>) can still find their fixture.
/// </summary>
internal static class WorkerEvalIdentity
{
    public const string Owner = "eval";
    public const string Repo = "reviewbot";

    public static string HeadSha(int prNumber) => $"evalhead{prNumber:D6}";

    public static string BaseSha(int prNumber) => $"evalbase{prNumber:D6}";

    public static int PrNumberFromSha(string sha) =>
        sha.Length >= 6 && int.TryParse(sha[^6..], out var prNumber)
            ? prNumber
            : throw new InvalidOperationException($"Eval head SHA '{sha}' does not carry a PR number.");
}
