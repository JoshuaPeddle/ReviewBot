using Microsoft.Extensions.FileSystemGlobbing;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Jobs;
using ReviewBot.Core.Llm;
using ReviewBot.GitHub.Auth;
using ReviewBot.GitHub.Config;
using ReviewBot.GitHub.Pulls;

namespace ReviewBot.Api.Workers;

public sealed class ReviewWorker : BackgroundService
{
    private const string SynchronizeReason = "synchronize";

    private readonly IReviewJobQueue queue;
    private readonly IInstallationTokenProvider tokenProvider;
    private readonly IPullRequestFetcher pullRequestFetcher;
    private readonly IRepoConfigFetcher repoConfigFetcher;
    private readonly IReviewLlmFactory llmFactory;
    private readonly IReviewPoster reviewPoster;
    private readonly ILogger<ReviewWorker> logger;

    public ReviewWorker(
        IReviewJobQueue queue,
        IInstallationTokenProvider tokenProvider,
        IPullRequestFetcher pullRequestFetcher,
        IRepoConfigFetcher repoConfigFetcher,
        IReviewLlmFactory llmFactory,
        IReviewPoster reviewPoster,
        ILogger<ReviewWorker> logger)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        this.pullRequestFetcher = pullRequestFetcher ?? throw new ArgumentNullException(nameof(pullRequestFetcher));
        this.repoConfigFetcher = repoConfigFetcher ?? throw new ArgumentNullException(nameof(repoConfigFetcher));
        this.llmFactory = llmFactory ?? throw new ArgumentNullException(nameof(llmFactory));
        this.reviewPoster = reviewPoster ?? throw new ArgumentNullException(nameof(reviewPoster));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in queue.DequeueAllAsync(stoppingToken).ConfigureAwait(false))
            {
                using var scope = logger.BeginScope(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["DeliveryId"] = job.DeliveryId,
                    ["Owner"] = job.Owner,
                    ["Repo"] = job.Repo,
                    ["PrNumber"] = job.PrNumber,
                    ["InstallationId"] = job.InstallationId,
                });

                try
                {
                    await ProcessAsync(job, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Review job {DeliveryId} for {Owner}/{Repo}#{PrNumber} failed; continuing with the next job",
                        job.DeliveryId,
                        job.Owner,
                        job.Repo,
                        job.PrNumber);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Review worker stopped");
        }
    }

    private async Task ProcessAsync(ReviewJob job, CancellationToken ct)
    {
        logger.LogInformation(
            "Processing review job {DeliveryId} for {Owner}/{Repo}#{PrNumber} because of {Reason}",
            job.DeliveryId,
            job.Owner,
            job.Repo,
            job.PrNumber,
            job.Reason);

        var installationToken = await tokenProvider.GetTokenAsync(job.InstallationId, ct).ConfigureAwait(false);
        var config = await repoConfigFetcher
            .FetchAsync(job.Owner, job.Repo, job.HeadSha, installationToken.Token, ct)
            .ConfigureAwait(false);

        if (!config.Enabled)
        {
            logger.LogInformation(
                "Skipping review job {DeliveryId} for {Owner}/{Repo}#{PrNumber} because ReviewBot is disabled by repo config",
                job.DeliveryId,
                job.Owner,
                job.Repo,
                job.PrNumber);
            return;
        }

        if (string.Equals(job.Reason, SynchronizeReason, StringComparison.Ordinal) &&
            !config.Review.Trigger.OnPush)
        {
            logger.LogInformation(
                "Skipping synchronize review job {DeliveryId} for {Owner}/{Repo}#{PrNumber} because on_push is disabled",
                job.DeliveryId,
                job.Owner,
                job.Repo,
                job.PrNumber);
            return;
        }

        var snapshot = await pullRequestFetcher
            .FetchAsync(job.Owner, job.Repo, job.PrNumber, installationToken.Token, config.Review.MaxFiles, ct)
            .ConfigureAwait(false);
        var files = ApplyIgnoreGlobs(snapshot.Files, config.Ignore);
        files = ApplyMaxFiles(files, config.Review.MaxFiles, job);

        var request = new ReviewRequest(
            snapshot.Title,
            snapshot.Body,
            snapshot.BaseSha,
            snapshot.HeadSha,
            files,
            config);
        var llm = llmFactory.Create(config.Model);
        var result = await llm.ReviewAsync(request, ct).ConfigureAwait(false);
        result = ApplyOutputConfig(result, config);

        await reviewPoster
            .PostAsync(job.Owner, job.Repo, job.PrNumber, snapshot.HeadSha, result, files, installationToken.Token, ct)
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<FileChange> ApplyIgnoreGlobs(
        IReadOnlyList<FileChange> files,
        IReadOnlyList<string> ignoreGlobs)
    {
        if (ignoreGlobs.Count == 0)
        {
            return files;
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        foreach (var pattern in ignoreGlobs.Where(pattern => !string.IsNullOrWhiteSpace(pattern)))
        {
            matcher.AddInclude(pattern);
        }

        return files
            .Where(file => !matcher.Match(file.Path).HasMatches)
            .ToArray();
    }

    private IReadOnlyList<FileChange> ApplyMaxFiles(
        IReadOnlyList<FileChange> files,
        int maxFiles,
        ReviewJob job)
    {
        if (files.Count <= maxFiles)
        {
            return files;
        }

        logger.LogWarning(
            "Review job {DeliveryId} for {Owner}/{Repo}#{PrNumber} has {FileCount} files after ignores; trimming to configured max_files {MaxFiles}",
            job.DeliveryId,
            job.Owner,
            job.Repo,
            job.PrNumber,
            files.Count,
            maxFiles);

        return files
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .Take(maxFiles)
            .ToArray();
    }

    private static ReviewResult ApplyOutputConfig(ReviewResult result, ReviewConfig config)
    {
        var summary = config.Review.Summary ? result.Summary : string.Empty;
        var comments = config.Review.InlineComments ? result.Comments : [];

        return summary == result.Summary && ReferenceEquals(comments, result.Comments)
            ? result
            : new ReviewResult(summary, comments);
    }
}
