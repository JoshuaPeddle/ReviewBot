using Octokit;
using ReviewBot.Core.Domain;

namespace ReviewBot.GitHub.Pulls;

public interface IReviewPoster
{
    Task PostAsync(
        string owner,
        string repo,
        int prNumber,
        string commitSha,
        ReviewResult result,
        IReadOnlyList<FileChange> files,
        string installationToken,
        CancellationToken ct,
        PullRequestReviewEvent reviewEvent = PullRequestReviewEvent.Comment);
}
