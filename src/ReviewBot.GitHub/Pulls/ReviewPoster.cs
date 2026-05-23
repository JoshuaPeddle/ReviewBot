using Microsoft.Extensions.Logging;
using Octokit;
using ReviewBot.Core.Domain;

namespace ReviewBot.GitHub.Pulls;

public sealed class ReviewPoster : IReviewPoster
{
    private const string DefaultBody = "Automated review by ReviewBot.";
    private const string GitHubJsonMediaType = "application/vnd.github+json";

    private readonly IGitHubClientFactory clientFactory;
    private readonly ILogger<ReviewPoster> logger;

    public ReviewPoster(IGitHubClientFactory clientFactory, ILogger<ReviewPoster> logger)
    {
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task PostAsync(
        string owner,
        string repo,
        int prNumber,
        string commitSha,
        ReviewResult result,
        IReadOnlyList<FileChange> files,
        string installationToken,
        CancellationToken ct)
    {
        ValidateInputs(owner, repo, prNumber, commitSha, result, files, installationToken);
        ct.ThrowIfCancellationRequested();

        var commentableLinesByPath = files.ToDictionary(
            file => file.Path,
            file => file.CommentableLines,
            StringComparer.Ordinal);
        var acceptedComments = new List<InlineComment>();
        var droppedCount = 0;

        foreach (var comment in result.Comments)
        {
            if (!commentableLinesByPath.TryGetValue(comment.Path, out var commentableLines))
            {
                droppedCount++;
                logger.LogInformation(
                    "Dropping review comment for unknown path {Path} on PR {Owner}/{Repo}#{PrNumber}",
                    comment.Path,
                    owner,
                    repo,
                    prNumber);
                continue;
            }

            if (!commentableLines.Contains(comment.Line))
            {
                droppedCount++;
                logger.LogInformation(
                    "Dropping review comment for non-commentable line {Line} in {Path} on PR {Owner}/{Repo}#{PrNumber}",
                    comment.Line,
                    comment.Path,
                    owner,
                    repo,
                    prNumber);
                continue;
            }

            acceptedComments.Add(comment);
        }

        var summary = result.Summary.Trim();
        if (acceptedComments.Count == 0 && summary.Length == 0)
        {
            logger.LogWarning(
                "Skipping review post for PR {Owner}/{Repo}#{PrNumber} because there is no summary or valid inline comment",
                owner,
                repo,
                prNumber);
            return;
        }

        var payload = BuildPayload(commitSha, summary, acceptedComments);
        var client = clientFactory.CreateForInstallation(installationToken);
        var reviewUri = BuildReviewUri(owner, repo, prNumber);

        try
        {
            // Octokit's typed review model only exposes diff positions for draft comments.
            // The raw connection lets us send GitHub's modern line/side payload directly.
            await client.Connection.Post(reviewUri, payload, GitHubJsonMediaType, ct).ConfigureAwait(false);
        }
        catch (ApiValidationException ex)
        {
            logger.LogError(
                ex,
                "GitHub rejected review post for PR {Owner}/{Repo}#{PrNumber}. AcceptedComments={AcceptedCount}; DroppedComments={DroppedCount}; StatusCode={StatusCode}; Message={Message}",
                owner,
                repo,
                prNumber,
                acceptedComments.Count,
                droppedCount,
                ex.StatusCode,
                ex.Message);

            throw new ReviewPostException(
                $"GitHub rejected the review payload with 422. Accepted comments: {acceptedComments.Count}; dropped comments: {droppedCount}.",
                acceptedComments.Count,
                droppedCount,
                ex);
        }
    }

    private static Dictionary<string, object> BuildPayload(
        string commitSha,
        string summary,
        IReadOnlyList<InlineComment> comments) =>
        new(StringComparer.Ordinal)
        {
            ["commit_id"] = commitSha,
            ["body"] = summary.Length == 0 ? DefaultBody : summary,
            ["event"] = "COMMENT",
            ["comments"] = comments
                .Select(comment => new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["path"] = comment.Path,
                    ["line"] = comment.Line,
                    ["side"] = comment.Side,
                    ["body"] = comment.Body,
                })
                .ToArray(),
        };

    private static Uri BuildReviewUri(string owner, string repo, int prNumber) =>
        new(
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls/{prNumber}/reviews",
            UriKind.Relative);

    private static void ValidateInputs(
        string owner,
        string repo,
        int prNumber,
        string commitSha,
        ReviewResult result,
        IReadOnlyList<FileChange> files,
        string installationToken)
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

        if (string.IsNullOrWhiteSpace(commitSha))
        {
            throw new ArgumentException("Commit SHA must be provided.", nameof(commitSha));
        }

        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(files);

        if (string.IsNullOrWhiteSpace(installationToken))
        {
            throw new ArgumentException("GitHub installation token must be provided.", nameof(installationToken));
        }
    }
}
