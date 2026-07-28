using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using ReviewBot.Core.Domain;
using ReviewBot.GitHub.Pulls;

namespace ReviewBot.GitHub.Checks;

public sealed class CheckRunFetcher : ICheckRunFetcher
{
    private const int MaxOutputChars = 4096;

    private readonly IGitHubClientFactory clientFactory;
    private readonly TimeProvider clock;
    private readonly ILogger<CheckRunFetcher> logger;

    public CheckRunFetcher(
        IGitHubClientFactory clientFactory,
        TimeProvider? clock = null,
        ILogger<CheckRunFetcher>? logger = null)
    {
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        this.clock = clock ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<CheckRunFetcher>.Instance;
    }

    public async Task<TestResult?> GetHeadCheckSummaryAsync(
        string owner,
        string repo,
        string headSha,
        string installationToken,
        CancellationToken ct)
    {
        ValidateInputs(owner, repo, headSha, installationToken);
        ct.ThrowIfCancellationRequested();

        var client = clientFactory.CreateForInstallation(installationToken);
        var checkRuns = await OctokitRateLimitRetry
            .ExecuteAsync(
                () => client.Check.Run.GetAllForReference(owner, repo, headSha),
                logger,
                clock,
                ct)
            .ConfigureAwait(false);
        var statuses = await OctokitRateLimitRetry
            .ExecuteAsync(
                () => client.Repository.Status.GetCombined(owner, repo, headSha),
                logger,
                clock,
                ct)
            .ConfigureAwait(false);

        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var completed = 0;
        var output = new StringBuilder();

        foreach (var checkRun in checkRuns.CheckRuns.Where(run => run.Status.Value == CheckStatus.Completed))
        {
            completed++;
            var conclusion = checkRun.Conclusion?.Value;
            switch (conclusion)
            {
                case CheckConclusion.Success:
                    passed++;
                    break;
                case CheckConclusion.Failure:
                case CheckConclusion.TimedOut:
                case CheckConclusion.Cancelled:
                case CheckConclusion.ActionRequired:
                    failed++;
                    break;
                case CheckConclusion.Neutral:
                case CheckConclusion.Skipped:
                case CheckConclusion.Stale:
                default:
                    skipped++;
                    break;
            }

            output.Append("- check ");
            output.Append(checkRun.Name);
            output.Append(": ");
            output.Append(conclusion?.ToString().ToLowerInvariant() ?? "unknown");
            output.Append('\n');
        }

        foreach (var status in statuses.Statuses.Where(status => status.State.Value != CommitState.Pending))
        {
            completed++;
            switch (status.State.Value)
            {
                case CommitState.Success:
                    passed++;
                    break;
                case CommitState.Error:
                case CommitState.Failure:
                    failed++;
                    break;
            }

            output.Append("- status ");
            output.Append(status.Context);
            output.Append(": ");
            output.Append(status.State.Value.ToString().ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(status.Description))
            {
                output.Append(" - ");
                output.Append(status.Description);
            }

            output.Append('\n');
        }

        return completed == 0
            ? null
            : new TestResult(passed, failed, skipped, Truncate(output.ToString().TrimEnd()), "github_checks");
    }

    private static string Truncate(string value) =>
        value.Length <= MaxOutputChars
            ? value
            : string.Concat(value.AsSpan(0, MaxOutputChars), "\n... (truncated)");

    private static void ValidateInputs(string owner, string repo, string headSha, string installationToken)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Repository owner must be provided.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository name must be provided.", nameof(repo));
        if (string.IsNullOrWhiteSpace(headSha))
            throw new ArgumentException("Head SHA must be provided.", nameof(headSha));
        if (string.IsNullOrWhiteSpace(installationToken))
            throw new ArgumentException("GitHub installation token must be provided.", nameof(installationToken));
    }
}
