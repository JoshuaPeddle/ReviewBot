using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Options;
using Octokit;
using ReviewBot.Core.Context;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Jobs;
using ReviewBot.Core.Llm;
using ReviewBot.Core.Prompting;
using ReviewBot.Core.Storage;
using ReviewBot.GitHub.Auth;
using ReviewBot.GitHub.Config;
using ReviewBot.GitHub.Pulls;
using ReviewBot.Grounding;

namespace ReviewBot.Api.Workers;

public sealed class ReviewWorker : BackgroundService
{
    private const string SynchronizeReason = "synchronize";
    private const double MostlyNewFileAdditionRatioThreshold = 0.9;

    private readonly IReviewJobQueue queue;
    private readonly IInstallationTokenProvider tokenProvider;
    private readonly IPullRequestFetcher pullRequestFetcher;
    private readonly IRepoConfigFetcher repoConfigFetcher;
    private readonly IReviewLlmFactory llmFactory;
    private readonly IReviewPoster reviewPoster;
    private readonly IGroundingProvider groundingProvider;
    private readonly IPrReviewStateStore prReviewStateStore;
    private readonly ReviewBotMetrics metrics;
    private readonly IModelContextRegistry modelContextRegistry;
    private readonly IPromptTokenEstimator tokenEstimator;
    private readonly WorkerOptions workerOptions;
    private readonly ILogger<ReviewWorker> logger;

    public ReviewWorker(
        IReviewJobQueue queue,
        IInstallationTokenProvider tokenProvider,
        IPullRequestFetcher pullRequestFetcher,
        IRepoConfigFetcher repoConfigFetcher,
        IReviewLlmFactory llmFactory,
        IReviewPoster reviewPoster,
        IGroundingProvider groundingProvider,
        IPrReviewStateStore prReviewStateStore,
        ReviewBotMetrics metrics,
        IModelContextRegistry modelContextRegistry,
        IPromptTokenEstimator tokenEstimator,
        IOptions<WorkerOptions> options,
        ILogger<ReviewWorker> logger)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        this.pullRequestFetcher = pullRequestFetcher ?? throw new ArgumentNullException(nameof(pullRequestFetcher));
        this.repoConfigFetcher = repoConfigFetcher ?? throw new ArgumentNullException(nameof(repoConfigFetcher));
        this.llmFactory = llmFactory ?? throw new ArgumentNullException(nameof(llmFactory));
        this.reviewPoster = reviewPoster ?? throw new ArgumentNullException(nameof(reviewPoster));
        this.groundingProvider = groundingProvider ?? throw new ArgumentNullException(nameof(groundingProvider));
        this.prReviewStateStore = prReviewStateStore ?? throw new ArgumentNullException(nameof(prReviewStateStore));
        this.metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        this.modelContextRegistry = modelContextRegistry ?? throw new ArgumentNullException(nameof(modelContextRegistry));
        this.tokenEstimator = tokenEstimator ?? throw new ArgumentNullException(nameof(tokenEstimator));
        this.workerOptions = options?.Value ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The installation-token provider uses per-installation semaphore gates; it is concurrency-safe.
        using var semaphore = new SemaphoreSlim(workerOptions.Concurrency, workerOptions.Concurrency);
        var inFlightTasks = new List<Task>();

        try
        {
            await foreach (var job in queue.DequeueAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await semaphore.WaitAsync(stoppingToken).ConfigureAwait(false);
                inFlightTasks.RemoveAll(t => t.IsCompleted);
                inFlightTasks.Add(RunJobAsync(job, semaphore, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Review worker stopped");
        }

        if (inFlightTasks.Count > 0)
        {
            await Task.WhenAll(inFlightTasks).ConfigureAwait(false);
        }
    }

    private async Task RunJobAsync(ReviewJob job, SemaphoreSlim semaphore, CancellationToken ct)
    {
        // Yield so the dispatch loop continues dequeuing the next job before this one begins.
        await Task.Yield();
        try
        {
            using var scope = logger.BeginScope(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["DeliveryId"] = job.DeliveryId,
                ["Owner"] = job.Owner,
                ["Repo"] = job.Repo,
                ["PrNumber"] = job.PrNumber,
                ["InstallationId"] = job.InstallationId,
            });

            var metricStatus = "failure";
            try
            {
                var status = await ProcessAsync(job, ct).ConfigureAwait(false);
                metricStatus = status == JobProcessStatus.Skipped ? "skipped" : "success";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
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

            metrics.RecordJobProcessed(metricStatus);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<JobProcessStatus> ProcessAsync(ReviewJob job, CancellationToken ct)
    {
        logger.LogInformation(
            "Processing review job {DeliveryId} for {Owner}/{Repo}#{PrNumber} because of {Reason}",
            job.DeliveryId,
            job.Owner,
            job.Repo,
            job.PrNumber,
            job.Reason);

        var installationToken = await tokenProvider.GetTokenAsync(job.InstallationId, ct).ConfigureAwait(false);

        // Comment-triggered reviews arrive without a head SHA; resolve it from the API before
        // fetching config. For push/open triggers the SHA comes from the event payload, so the
        // repo config and current PR metadata can be fetched concurrently after token resolution.
        PullRequestMetadata? prefetchedMetadata = null;
        Task<PullRequestMetadata>? metadataTask = null;
        var configSha = job.HeadSha;
        if (configSha is null)
        {
            prefetchedMetadata = await pullRequestFetcher
                .FetchMetadataAsync(job.Owner, job.Repo, job.PrNumber, installationToken.Token, ct)
                .ConfigureAwait(false);
            configSha = prefetchedMetadata.HeadSha;
        }
        else
        {
            metadataTask = pullRequestFetcher
                .FetchMetadataAsync(job.Owner, job.Repo, job.PrNumber, installationToken.Token, ct);
        }

        var configTask = repoConfigFetcher
            .FetchAsync(job.Owner, job.Repo, configSha, installationToken.Token, ct);
        ReviewConfig config;
        try
        {
            config = await configTask.ConfigureAwait(false);
        }
        catch
        {
            LogIfBackgroundTaskFails(metadataTask, "PR metadata fetch");
            throw;
        }

        if (!config.Enabled)
        {
            LogIfBackgroundTaskFails(metadataTask, "PR metadata fetch");
            logger.LogInformation(
                "Skipping review job {DeliveryId} for {Owner}/{Repo}#{PrNumber} because ReviewBot is disabled by repo config",
                job.DeliveryId,
                job.Owner,
                job.Repo,
                job.PrNumber);
            metrics.RecordSkip("disabled");
            return JobProcessStatus.Skipped;
        }

        if (string.Equals(job.Reason, SynchronizeReason, StringComparison.Ordinal) &&
            !config.Review.Trigger.OnPush)
        {
            LogIfBackgroundTaskFails(metadataTask, "PR metadata fetch");
            logger.LogInformation(
                "Skipping synchronize review job {DeliveryId} for {Owner}/{Repo}#{PrNumber} because on_push is disabled",
                job.DeliveryId,
                job.Owner,
                job.Repo,
                job.PrNumber);
            metrics.RecordSkip("trigger_disabled");
            return JobProcessStatus.Skipped;
        }

        var repoFullName = $"{job.Owner}/{job.Repo}";
        var lastShaTask = prReviewStateStore
            .GetLastShaAsync(job.InstallationId, repoFullName, job.PrNumber, ct);
        var metadata = prefetchedMetadata ?? await metadataTask!.ConfigureAwait(false);
        var lastSha = await lastShaTask.ConfigureAwait(false);

        IReadOnlySet<string>? allowlist = null;
        var incrementalType = "first_review";

        if (lastSha is not null && !string.Equals(lastSha, metadata.HeadSha, StringComparison.Ordinal))
        {
            incrementalType = "delta_review";
            try
            {
                var compareResult = await pullRequestFetcher
                    .GetChangedFilesSinceAsync(job.Owner, job.Repo, lastSha, metadata.HeadSha, installationToken.Token, ct)
                    .ConfigureAwait(false);

                if (!compareResult.IsComplete)
                {
                    logger.LogWarning(
                        "Compare result for {Owner}/{Repo}#{PrNumber} is truncated ({Count} files); falling back to full file list",
                        job.Owner,
                        job.Repo,
                        job.PrNumber,
                        compareResult.Paths.Count);
                    incrementalType = "compare_truncated_fallback";
                }
                else if (compareResult.Paths.Count == 0)
                {
                    logger.LogDebug(
                        "No files changed since last review (SHA {LastSha}) for {Owner}/{Repo}#{PrNumber}; skipping",
                        lastSha,
                        job.Owner,
                        job.Repo,
                        job.PrNumber);
                    metrics.RecordIncrementalReview("no_changes");
                    metrics.RecordSkip("incremental_no_changes");
                    await prReviewStateStore
                        .SetLastShaAsync(job.InstallationId, repoFullName, job.PrNumber, metadata.HeadSha, ct)
                        .ConfigureAwait(false);
                    return JobProcessStatus.Skipped;
                }
                else
                {
                    allowlist = new HashSet<string>(compareResult.Paths, StringComparer.Ordinal);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "Compare API call failed for {Owner}/{Repo}#{PrNumber}; falling back to full file list",
                    job.Owner,
                    job.Repo,
                    job.PrNumber);
                incrementalType = "compare_failed_fallback";
            }
        }

        var rawFiles = await pullRequestFetcher
            .FetchFilesAsync(job.Owner, job.Repo, job.PrNumber, installationToken.Token, config.Review.MaxFiles, allowlist, ct)
            .ConfigureAwait(false);
        var files = ApplyIgnoreGlobs(rawFiles, config.Ignore);
        files = ApplyMaxFiles(files, config.Review.MaxFiles, job);
        var patchBudgetResult = ApplyPatchBudget(files, config.Review.MaxPatchLines, job);
        files = patchBudgetResult.Files;

        if (files.Count == 0)
        {
            logger.LogInformation(
                "Skipping review job {DeliveryId} for {Owner}/{Repo}#{PrNumber}: no reviewable files after filtering",
                job.DeliveryId,
                job.Owner,
                job.Repo,
                job.PrNumber);
            metrics.RecordSkip("no_reviewable_files");
            return JobProcessStatus.Skipped;
        }

        var groundingRequest = new GroundingRequest(
            Owner: job.Owner,
            Repo: job.Repo,
            HeadSha: metadata.HeadSha,
            InstallationToken: installationToken.Token,
            Config: config.Grounding);
        var grounding = await GetGroundingContextAsync(groundingRequest, job, ct).ConfigureAwait(false);
        var promptBudget = CreatePromptBudget(config, grounding, metadata, job);
        var fullFileContext = await FetchFullFileContentsAsync(
                files,
                config,
                promptBudget,
                job,
                metadata.HeadSha,
                installationToken.Token,
                ct)
            .ConfigureAwait(false);
        var fullFileContents = fullFileContext.Contents;
        promptBudget = fullFileContext.Budget;
        promptBudget = ConsumeDiffBudget(files, config, promptBudget, job);
        LogPromptBudget(promptBudget, config, job);

        var request = new ReviewRequest(
            metadata.Title,
            metadata.Body,
            metadata.BaseSha,
            metadata.HeadSha,
            files,
            config,
            grounding,
            fullFileContents);

        var llm = llmFactory.Create(config.Model);
        var sw = Stopwatch.StartNew();
        var result = await llm.ReviewAsync(request, ct).ConfigureAwait(false);
        sw.Stop();
        logger.LogInformation(
            "LLM review completed in {LlmDurationMs}ms for {DeliveryId}",
            sw.Elapsed.TotalMilliseconds,
            job.DeliveryId);
        metrics.RecordLlmDuration(sw.Elapsed.TotalMilliseconds, config.Model.Provider);

        var initialResult = result;
        var initialCandidateComments = FilterCandidateComments(initialResult, config);
        var speculativeSelfCritique = StartSelfCritiqueIfNeeded(llm, files, initialCandidateComments, config, ct);

        try
        {
            result = await ApplyAgenticContextAsync(
                    llm,
                    request,
                    result,
                    config,
                    job,
                    metadata.HeadSha,
                    installationToken.Token,
                    ct)
                .ConfigureAwait(false);
        }
        catch
        {
            CancelSelfCritique(speculativeSelfCritique);
            throw;
        }
        result = AppendFilesSkippedNote(result, patchBudgetResult.SkippedPaths);
        IReadOnlyList<InlineComment> candidateComments;
        if (ReferenceEquals(result, initialResult))
        {
            candidateComments = speculativeSelfCritique is null
                ? initialCandidateComments
                : await AwaitSelfCritiqueAsync(speculativeSelfCritique).ConfigureAwait(false);
        }
        else
        {
            CancelSelfCritique(speculativeSelfCritique);
            candidateComments = FilterCandidateComments(result, config);
            candidateComments = await ApplySelfCritiqueAsync(llm, files, candidateComments, config, ct)
                .ConfigureAwait(false);
        }

        result = ApplyOutputConfig(result, candidateComments, config);
        if (config.Review.Summary)
        {
            result = AppendRereviewHint(result);
        }

        var reviewEvent = DetermineReviewEvent(result.Comments, config);
        metrics.RecordCommentsPosted(result.Comments.Count);

        await reviewPoster
            .PostAsync(job.Owner, job.Repo, job.PrNumber, metadata.HeadSha, result, files, installationToken.Token, ct, reviewEvent)
            .ConfigureAwait(false);

        await prReviewStateStore
            .SetLastShaAsync(job.InstallationId, repoFullName, job.PrNumber, metadata.HeadSha, ct)
            .ConfigureAwait(false);
        metrics.RecordIncrementalReview(incrementalType);

        return JobProcessStatus.Success;
    }

    private enum JobProcessStatus { Success, Skipped }

    private sealed record FullFileContextResult(
        IReadOnlyDictionary<string, string>? Contents,
        PromptBudget Budget);

    private SelfCritiqueRun? StartSelfCritiqueIfNeeded(
        IReviewLlm llm,
        IReadOnlyList<FileChange> files,
        IReadOnlyList<InlineComment> candidateComments,
        ReviewConfig config,
        CancellationToken ct)
    {
        if (!ShouldRunSelfCritique(candidateComments, config))
        {
            return null;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = ApplySelfCritiqueAsync(llm, files, candidateComments, config, cts.Token);
        return new SelfCritiqueRun(task, cts);
    }

    private static async Task<IReadOnlyList<InlineComment>> AwaitSelfCritiqueAsync(SelfCritiqueRun run)
    {
        try
        {
            return await run.Task.ConfigureAwait(false);
        }
        finally
        {
            run.Cancellation.Dispose();
        }
    }

    private void CancelSelfCritique(SelfCritiqueRun? run)
    {
        if (run is null)
        {
            return;
        }

        run.Cancellation.Cancel();
        _ = LogAndDisposeBackgroundTaskAsync(run.Task, run.Cancellation, "speculative self-critique");
    }

    private async Task LogAndDisposeBackgroundTaskAsync(
        Task task,
        CancellationTokenSource cts,
        string operationName)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The speculative call was superseded by a later review result.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{OperationName} failed after the review job no longer needed its result", operationName);
        }
        finally
        {
            cts.Dispose();
        }
    }

    private void LogIfBackgroundTaskFails(Task? task, string operationName)
    {
        if (task is null)
        {
            return;
        }

        _ = LogIfBackgroundTaskFailsAsync(task, operationName);
    }

    private async Task LogIfBackgroundTaskFailsAsync(Task task, string operationName)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The review job is already exiting; cancellation is expected and does not need a warning.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{OperationName} failed after the review job no longer needed its result", operationName);
        }
    }

    private async Task<GroundingContext> GetGroundingContextAsync(
        GroundingRequest request,
        ReviewJob job,
        CancellationToken ct)
    {
        var groundingSw = Stopwatch.StartNew();
        var grounding = await groundingProvider.GetContextAsync(request, ct).ConfigureAwait(false);
        groundingSw.Stop();
        var groundingResult = grounding.Tests is not null
            ? (grounding.Tests.Failed == 0 ? "checks_success" : "checks_failed")
            : grounding.Build is not null
            ? (grounding.Build.Success ? "build_success" : "build_failed")
            : grounding.Language is not null ? "tier1" : "none";
        metrics.RecordGroundingDuration(groundingSw.Elapsed.TotalMilliseconds, groundingResult);

        if (grounding.Language is { } language)
        {
            logger.LogDebug(
                "Grounding detected {LanguageId} {LanguageVersion} for {Owner}/{Repo}",
                language.LanguageId,
                language.LanguageVersion,
                job.Owner,
                job.Repo);
        }

        return grounding;
    }

    private PromptBudget CreatePromptBudget(
        ReviewConfig config,
        GroundingContext grounding,
        PullRequestMetadata metadata,
        ReviewJob job)
    {
        var estimationRequestWithoutGrounding = new ReviewRequest(
            metadata.Title,
            metadata.Body,
            metadata.BaseSha,
            metadata.HeadSha,
            [],
            config);
        var estimationRequestWithGrounding = estimationRequestWithoutGrounding with
        {
            Grounding = grounding
        };

        var baseSystemPrompt = PromptBuilder.Build(estimationRequestWithoutGrounding).SystemPrompt;
        var groundedSystemPrompt = PromptBuilder.Build(estimationRequestWithGrounding).SystemPrompt;
        var systemPromptTokens = tokenEstimator.EstimateTokens(baseSystemPrompt);
        var groundingTokens = Math.Max(
            0,
            tokenEstimator.EstimateTokens(groundedSystemPrompt) - systemPromptTokens);

        var budget = PromptBudget.Create(
            modelContextRegistry.GetContextWindowTokens(config.Model.Name),
            systemPromptTokens,
            groundingTokens,
            config.Review.ResponseReserveTokens);

        var metadataTokens = tokenEstimator.EstimateTokens(metadata.Title) +
            tokenEstimator.EstimateTokens(metadata.Body);
        var updated = budget.ConsumeAvailable("pull_request_metadata", metadataTokens, out var consumedTokens);
        if (consumedTokens < metadataTokens)
        {
            logger.LogWarning(
                "Review job {DeliveryId} for {Owner}/{Repo}#{PrNumber} has estimated PR metadata cost of {MetadataTokens} token(s), exceeding the remaining prompt budget of {RemainingTokens} token(s) for model {ModelName}",
                job.DeliveryId,
                job.Owner,
                job.Repo,
                job.PrNumber,
                metadataTokens,
                budget.RemainingContentTokens,
                config.Model.Name);
        }

        return updated;
    }

    private PromptBudget ConsumeDiffBudget(
        IReadOnlyList<FileChange> files,
        ReviewConfig config,
        PromptBudget budget,
        ReviewJob job)
    {
        var diffTokens = EstimateDiffTokens(files, config.Review.MaxPatchLines);
        var updated = budget.ConsumeAvailable("diff", diffTokens, out var consumedTokens);
        if (consumedTokens < diffTokens)
        {
            logger.LogWarning(
                "Review job {DeliveryId} for {Owner}/{Repo}#{PrNumber} has an estimated diff cost of {DiffTokens} token(s), exceeding the remaining prompt budget of {RemainingTokens} token(s) for model {ModelName}",
                job.DeliveryId,
                job.Owner,
                job.Repo,
                job.PrNumber,
                diffTokens,
                budget.RemainingContentTokens,
                config.Model.Name);
        }

        return updated;
    }

    private int EstimateDiffTokens(IReadOnlyList<FileChange> files, int maxPatchLines)
    {
        var tokens = 0;
        foreach (var file in files)
        {
            tokens += tokenEstimator.EstimateTokens(
                $"{file.Path} {file.Status} +{file.AdditionsCount} -{file.DeletionsCount}\n{TakePatchLines(file.Patch, maxPatchLines)}");
        }

        return tokens;
    }

    private void LogPromptBudget(PromptBudget budget, ReviewConfig config, ReviewJob job)
    {
        logger.LogDebug(
            "Prompt budget for {Owner}/{Repo}#{PrNumber} on {ModelName}: model limit {ModelLimitTokens}, system {SystemPromptTokens}, grounding {GroundingTokens}, response reserve {ResponseReserveTokens}, content budget {ContentBudgetTokens}, consumed {ConsumedContentTokens}, remaining {RemainingContentTokens}, sections {Sections}",
            job.Owner,
            job.Repo,
            job.PrNumber,
            config.Model.Name,
            budget.ModelContextLimitTokens,
            budget.SystemPromptTokens,
            budget.GroundingTokens,
            budget.ResponseReserveTokens,
            budget.ContentBudgetTokens,
            budget.ConsumedContentTokens,
            budget.RemainingContentTokens,
            string.Join(", ", budget.ConsumedSections.Select(section => $"{section.Name}={section.Tokens}")));
    }

    private async Task<FullFileContextResult> FetchFullFileContentsAsync(
        IReadOnlyList<FileChange> files,
        ReviewConfig config,
        PromptBudget budget,
        ReviewJob job,
        string headSha,
        string installationToken,
        CancellationToken ct)
    {
        if (config.Review.FullFileMaxBytes <= 0)
        {
            return new FullFileContextResult(null, budget);
        }

        var candidates = files
            .Where(file => file.Status != FileChangeStatus.Removed)
            .Where(file => !IsMostlyNewFile(file))
            .Where(file => EstimatePatchBytes(file) <= config.Review.FullFileMaxBytes)
            .ToArray();

        var selectedRequests = new List<ContextRequest>();
        var selectionBudget = budget;
        var loggedBudgetLimitedSelection = false;
        foreach (var file in candidates)
        {
            var estimatedTokens = tokenEstimator.EstimateTokens(file.Patch);
            if (!selectionBudget.TryConsume("full_file_request_estimate", estimatedTokens, out var updatedBudget))
            {
                if (!loggedBudgetLimitedSelection)
                {
                    logger.LogWarning(
                        "Full-file context for {Owner}/{Repo}#{PrNumber} is limited by prompt budget; skipping candidate {Path} because its estimated patch cost is {EstimatedTokens} token(s) and only {RemainingTokens} token(s) remain",
                        job.Owner,
                        job.Repo,
                        job.PrNumber,
                        file.Path,
                        estimatedTokens,
                        selectionBudget.RemainingContentTokens);
                    loggedBudgetLimitedSelection = true;
                }

                continue;
            }

            selectionBudget = updatedBudget;
            selectedRequests.Add(new ContextRequest(file.Path, "full-file context for small changed file"));
        }

        var requests = selectedRequests.ToArray();

        if (requests.Length == 0)
        {
            logger.LogDebug(
                "Full-file context enabled for {Owner}/{Repo}#{PrNumber}, but no changed files fit under the {MaxBytes} byte and {RemainingTokens} token limits",
                job.Owner,
                job.Repo,
                job.PrNumber,
                config.Review.FullFileMaxBytes,
                budget.RemainingContentTokens);
            return new FullFileContextResult(null, budget);
        }

        IReadOnlyList<(string Path, string Content)> fetchedFiles;
        try
        {
            fetchedFiles = await pullRequestFetcher
                .GetFileContentsAsync(
                    job.Owner,
                    job.Repo,
                    requests,
                    headSha,
                    config.Review.FullFileMaxBytes,
                    installationToken,
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Full-file context fetch failed; continuing with diff-only prompt");
            return new FullFileContextResult(null, budget);
        }

        if (fetchedFiles.Count == 0)
        {
            logger.LogInformation(
                "Full-file context: {CandidateCount} candidate file(s) for {Owner}/{Repo}#{PrNumber} but none could be fetched (404, binary, or oversized)",
                requests.Length,
                job.Owner,
                job.Repo,
                job.PrNumber);
            return new FullFileContextResult(null, budget);
        }

        var included = new Dictionary<string, string>(StringComparer.Ordinal);
        var updated = budget;
        foreach (var fetchedFile in fetchedFiles)
        {
            var estimatedTokens = tokenEstimator.EstimateTokens(fetchedFile.Content);
            if (!updated.TryConsume("full_file", estimatedTokens, out var afterFullFile))
            {
                logger.LogDebug(
                    "Full-file context: dropping {Path} for {Owner}/{Repo}#{PrNumber} because it needs {EstimatedTokens} token(s) and only {RemainingTokens} remain",
                    fetchedFile.Path,
                    job.Owner,
                    job.Repo,
                    job.PrNumber,
                    estimatedTokens,
                    updated.RemainingContentTokens);
                continue;
            }

            updated = afterFullFile;
            included[fetchedFile.Path] = fetchedFile.Content;
        }

        if (included.Count == 0)
        {
            logger.LogInformation(
                "Full-file context: fetched {FetchedCount} candidate file(s) for {Owner}/{Repo}#{PrNumber} but none fit the remaining prompt budget",
                fetchedFiles.Count,
                job.Owner,
                job.Repo,
                job.PrNumber);
            return new FullFileContextResult(null, budget);
        }

        logger.LogInformation(
            "Full-file context: included {IncludedCount}/{FetchedCount} fetched file(s) for {Owner}/{Repo}#{PrNumber}",
            included.Count,
            fetchedFiles.Count,
            job.Owner,
            job.Repo,
            job.PrNumber);

        return new FullFileContextResult(included, updated);
    }

    private static string TakePatchLines(string patch, int maxPatchLines)
    {
        if (maxPatchLines <= 0 || string.IsNullOrEmpty(patch))
        {
            return string.Empty;
        }

        return string.Join('\n', patch
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Take(maxPatchLines));
    }

    private static int EstimatePatchBytes(FileChange file) => Encoding.UTF8.GetByteCount(file.Patch);

    private static bool IsMostlyNewFile(FileChange file)
    {
        var changedLines = file.AdditionsCount + file.DeletionsCount;
        if (changedLines <= 0)
        {
            return false;
        }

        return (double)file.AdditionsCount / changedLines > MostlyNewFileAdditionRatioThreshold;
    }

    private static PullRequestReviewEvent DetermineReviewEvent(
        IReadOnlyList<InlineComment> finalComments,
        ReviewConfig config)
    {
        if (config.Review.RequestChangesOnError && finalComments.Any(c => c.Severity == Severity.Error))
        {
            return PullRequestReviewEvent.RequestChanges;
        }

        if (config.Review.ApproveIfClean && finalComments.Count == 0)
        {
            return PullRequestReviewEvent.Approve;
        }

        return PullRequestReviewEvent.Comment;
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

    private PatchBudgetResult ApplyPatchBudget(
        IReadOnlyList<FileChange> files,
        int maxPatchLines,
        ReviewJob job)
    {
        if (files.Count == 0)
        {
            return new PatchBudgetResult(files, []);
        }

        var budget = (long)maxPatchLines * 5;
        var fileLineCounts = files
            .Select(file => new FilePatchLineCount(file, CountPatchLines(file.Patch)))
            .ToArray();
        var totalPatchLines = fileLineCounts.Sum(file => file.LineCount);

        if (totalPatchLines <= budget)
        {
            return new PatchBudgetResult(files, []);
        }

        var selected = new List<FileChange>();
        var selectedPaths = new HashSet<string>(StringComparer.Ordinal);
        var accumulatedLines = 0L;

        foreach (var fileLineCount in fileLineCounts
            .OrderBy(file => file.LineCount)
            .ThenBy(file => file.File.Path, StringComparer.Ordinal))
        {
            if (accumulatedLines + fileLineCount.LineCount > budget)
            {
                continue;
            }

            selected.Add(fileLineCount.File);
            selectedPaths.Add(fileLineCount.File.Path);
            accumulatedLines += fileLineCount.LineCount;
        }

        var skippedPaths = files
            .Where(file => !selectedPaths.Contains(file.Path))
            .Select(file => file.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        logger.LogWarning(
            "Review job {DeliveryId} for {Owner}/{Repo}#{PrNumber} has {TotalPatchLines} patch lines; keeping {KeptFileCount} files within budget {PatchLineBudget} and skipping {SkippedFileCount} files",
            job.DeliveryId,
            job.Owner,
            job.Repo,
            job.PrNumber,
            totalPatchLines,
            selected.Count,
            budget,
            skippedPaths.Length);

        return new PatchBudgetResult(selected.ToArray(), skippedPaths);
    }

    private static long CountPatchLines(string patch)
    {
        if (patch.Length == 0)
        {
            return 0;
        }

        var normalizedPatch = patch.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var count = 1L;
        foreach (var character in normalizedPatch)
        {
            if (character == '\n')
            {
                count++;
            }
        }

        return normalizedPatch.EndsWith('\n') ? count - 1 : count;
    }

    private static ReviewResult AppendFilesSkippedNote(
        ReviewResult result,
        IReadOnlyList<string> skippedPaths)
    {
        if (skippedPaths.Count == 0)
        {
            return result;
        }

        var note = "files_skipped: The following files were omitted from automated review because the pull request exceeded the configured patch budget: "
            + string.Join(", ", skippedPaths.Select(path => $"`{path}`"))
            + ".";
        var summary = string.IsNullOrWhiteSpace(result.Summary)
            ? note
            : $"{result.Summary.TrimEnd()}\n\n{note}";

        return new ReviewResult(summary, result.Comments, result.ContextRequests);
    }

    private static ReviewResult AppendRereviewHint(ReviewResult result)
    {
        const string hint = "*To re-request a review, comment `/review`.*";
        var summary = string.IsNullOrWhiteSpace(result.Summary)
            ? hint
            : $"{result.Summary.TrimEnd()}\n\n---\n{hint}";
        return new ReviewResult(summary, result.Comments, result.ContextRequests);
    }

    private async Task<ReviewResult> ApplyAgenticContextAsync(
        IReviewLlm llm,
        ReviewRequest request,
        ReviewResult initialResult,
        ReviewConfig config,
        ReviewJob job,
        string headSha,
        string installationToken,
        CancellationToken ct)
    {
        if (!config.Review.AgenticContext || initialResult.ContextRequests.Count == 0)
        {
            return initialResult;
        }

        var validation = FilterContextRequests(initialResult.ContextRequests, config);
        LogContextRequestDrops(validation.DropCounts, initialResult.ContextRequests.Count, validation.Requests.Count, job);

        if (validation.Requests.Count == 0)
        {
            return initialResult;
        }

        logger.LogInformation(
            "Agentic context: model requested {RequestCount} file(s) for {Owner}/{Repo}#{PrNumber}: {Paths}",
            validation.Requests.Count,
            job.Owner,
            job.Repo,
            job.PrNumber,
            string.Join(", ", validation.Requests.Select(r => r.Path)));

        IReadOnlyList<(string Path, string Content)> fetchedFiles;
        try
        {
            fetchedFiles = await pullRequestFetcher
                .GetFileContentsAsync(
                    job.Owner,
                    job.Repo,
                    validation.Requests,
                    headSha,
                    config.Review.MaxContextFileBytes,
                    installationToken,
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Agentic context file fetch failed; using initial comments");
            return initialResult;
        }

        if (fetchedFiles.Count == 0)
        {
            logger.LogInformation(
                "Agentic context: requested {RequestCount} file(s) for {Owner}/{Repo}#{PrNumber} but none could be fetched (404, binary, or oversized); using initial comments",
                validation.Requests.Count,
                job.Owner,
                job.Repo,
                job.PrNumber);
            return initialResult;
        }

        logger.LogInformation(
            "Agentic context: fetched {FetchedCount}/{RequestCount} file(s) for {Owner}/{Repo}#{PrNumber}, running second-pass review: {Paths}",
            fetchedFiles.Count,
            validation.Requests.Count,
            job.Owner,
            job.Repo,
            job.PrNumber,
            string.Join(", ", fetchedFiles.Select(f => f.Path)));

        try
        {
            var enrichedPayload = PromptBuilder.BuildContextEnrichedRequest(request, initialResult, fetchedFiles);
            var contextSw = Stopwatch.StartNew();
            var enrichedRaw = await llm.CompleteRawAsync(enrichedPayload, ct, "agentic_context").ConfigureAwait(false);
            contextSw.Stop();
            metrics.RecordLlmDuration(
                contextSw.Elapsed.TotalMilliseconds,
                config.Model.Provider,
                "agentic_context");

            var enrichedParsed = LlmResultParser.Parse(enrichedRaw, logger);
            if (enrichedParsed.Success)
            {
                logger.LogInformation(
                    "Agentic context: second-pass review completed for {Owner}/{Repo}#{PrNumber}; {CommentCount} comment(s) in final result",
                    job.Owner,
                    job.Repo,
                    job.PrNumber,
                    enrichedParsed.Value!.Comments.Count);
                return enrichedParsed.Value!;
            }

            logger.LogWarning(
                "Agentic context second-pass response was invalid: {Error}; using initial comments",
                enrichedParsed.Error);
            return initialResult;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Agentic context second pass failed; using initial comments");
            return initialResult;
        }
    }

    private async Task<IReadOnlyList<InlineComment>> ApplySelfCritiqueAsync(
        IReviewLlm llm,
        IReadOnlyList<FileChange> files,
        IReadOnlyList<InlineComment> candidateComments,
        ReviewConfig config,
        CancellationToken ct)
    {
        if (!ShouldRunSelfCritique(candidateComments, config))
        {
            return candidateComments;
        }

        var highConfidence = candidateComments
            .Where(c => c.Confidence == Confidence.High)
            .ToArray();
        var critiqueCandidates = candidateComments
            .Where(c => c.Confidence != Confidence.High)
            .ToArray();

        var critiquePayload = SelfCritiquePromptBuilder.Build(files, critiqueCandidates);
        try
        {
            var critiqueSw = Stopwatch.StartNew();
            var rawCritique = await llm.CompleteRawAsync(critiquePayload, ct, "self_critique").ConfigureAwait(false);
            critiqueSw.Stop();
            metrics.RecordLlmDuration(
                critiqueSw.Elapsed.TotalMilliseconds,
                config.Model.Provider,
                "self_critique");

            var critique = SelfCritiqueParser.Parse(rawCritique, critiqueCandidates.Length);
            if (critique is null)
            {
                logger.LogWarning("Self-critique response was invalid; using full initial comment set");
                return candidateComments;
            }

            var retained = critique.RetainedIndices
                .Select(i => critiqueCandidates[i])
                .ToArray();
            logger.LogDebug(
                "Self-critique retained {Retained}/{Total} lower-confidence comments. Rationale: {Rationale}",
                retained.Length,
                critiqueCandidates.Length,
                critique.Rationale);

            return highConfidence.Concat(retained).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Self-critique failed; using full initial comment set");
            return candidateComments;
        }
    }

    private static bool ShouldRunSelfCritique(IReadOnlyList<InlineComment> candidateComments, ReviewConfig config) =>
        config.Review.SelfCritique &&
        candidateComments.Any(c => c.Confidence != Confidence.High);

    private static IReadOnlyList<InlineComment> FilterCandidateComments(ReviewResult result, ReviewConfig config)
    {
        if (!config.Review.InlineComments)
        {
            return Array.Empty<InlineComment>();
        }

        return result.Comments
            .Where(c => c.Confidence >= config.Review.MinConfidence)
            .Where(c => !IsPraiseOnlyComment(c.Body))
            .Where(c => !IsMetaReviewComment(c.Body))
            .Where(c => !IsSpeculativeMissingContextComment(c.Body))
            .ToArray();
    }

    private static ReviewResult ApplyOutputConfig(
        ReviewResult result,
        IReadOnlyList<InlineComment> comments,
        ReviewConfig config)
    {
        var summary = config.Review.Summary ? result.Summary : string.Empty;
        if (IsPositiveOnlySummary(summary))
        {
            summary = string.Empty;
        }

        return summary == result.Summary && ReferenceEquals(comments, result.Comments)
            ? result
            : new ReviewResult(summary, comments, result.ContextRequests);
    }

    private static bool IsPraiseOnlyComment(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var normalized = NormalizeForTextHeuristics(body);
        return ContainsAny(normalized, PraiseCommentPhrases) &&
            !ContainsAny(normalized, ActionableConcernPhrases);
    }

    private static bool IsPositiveOnlySummary(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return false;
        }

        var normalized = NormalizeForTextHeuristics(summary);
        return ContainsAny(normalized, PositiveSummaryPhrases) &&
            !ContainsAny(normalized, ActionableConcernPhrases);
    }

    private static bool IsMetaReviewComment(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var normalized = NormalizeForTextHeuristics(body);
        var referencesEvalArtifact = ContainsAny(normalized, EvalArtifactPhrases);
        var validatesExpectedOutcome = ContainsAny(normalized, ExpectedOutcomePhrases);

        return referencesEvalArtifact && validatesExpectedOutcome;
    }

    private static bool IsSpeculativeMissingContextComment(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var normalized = NormalizeForTextHeuristics(body);
        var asksToVerifyMissingContract = ContainsAny(normalized, MissingContractDirectivePhrases);
        var usesSpeculativeLanguage = ContainsAny(normalized, SpeculativeLanguagePhrases);
        var referencesUnseenContract = ContainsAny(normalized, UnseenContractPhrases);

        return asksToVerifyMissingContract && usesSpeculativeLanguage && referencesUnseenContract;
    }

    private static string NormalizeForTextHeuristics(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append(' ');
        foreach (var character in value)
        {
            sb.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        sb.Append(' ');
        return sb.ToString();
    }

    private static bool ContainsAny(string normalizedText, IReadOnlyList<string> phrases) =>
        phrases.Any(phrase => normalizedText.Contains(phrase, StringComparison.Ordinal));

    private static readonly string[] PraiseCommentPhrases =
    [
        " appropriate ",
        " appropriately ",
        " correct ",
        " correctly ",
        " correctly validates ",
        " excellent ",
        " good coverage ",
        " good guard ",
        " good test ",
        " great ",
        " guards against ",
        " helpful ",
        " looks good ",
        " nice ",
        " properly ",
        " reasonable ",
        " solid ",
        " useful ",
        " validates that ",
        " well done ",
        " well written "
    ];

    private static readonly string[] PositiveSummaryPhrases =
    [
        " clean ",
        " good overall ",
        " looks good ",
        " looks solid ",
        " no actionable ",
        " no concerns ",
        " no issues ",
        " nothing to flag ",
        " solid "
    ];

    private static readonly string[] EvalArtifactPhrases =
    [
        " expected yaml ",
        " expected finding ",
        " expected findings ",
        " fixture ",
        " fixtures "
    ];

    private static readonly string[] ExpectedOutcomePhrases =
    [
        " correctly models ",
        " expected yaml requires ",
        " requires mentioning ",
        " requires that ",
        " should expect "
    ];

    private static readonly string[] MissingContractDirectivePhrases =
    [
        " check whether ",
        " confirm ",
        " ensure ",
        " make sure ",
        " verify "
    ];

    private static readonly string[] SpeculativeLanguagePhrases =
    [
        " could ",
        " depending on ",
        " if ",
        " may ",
        " might "
    ];

    private static readonly string[] UnseenContractPhrases =
    [
        " async behavior ",
        " async await ",
        " contract ",
        " is async ",
        " method s return type ",
        " return type ",
        " side effects "
    ];

    private static readonly string[] ActionableConcernPhrases =
    [
        " add ",
        " avoid ",
        " breaks ",
        " bug ",
        " but ",
        " change ",
        " consider ",
        " could ",
        " deadlock ",
        " does not ",
        " doesn t ",
        " exception ",
        " fail ",
        " fails ",
        " fix ",
        " however ",
        " incorrect ",
        " issue ",
        " leak ",
        " may ",
        " might ",
        " missing ",
        " needs ",
        " not ",
        " null ",
        " problem ",
        " race ",
        " regress ",
        " remove ",
        " restore ",
        " risk ",
        " should ",
        " throw ",
        " unsafe ",
        " unless ",
        " vulnerable ",
        " wrong "
    ];

    private static ContextRequestValidationResult FilterContextRequests(
        IReadOnlyList<ContextRequest> requests,
        ReviewConfig config)
    {
        if (config.Review.MaxContextRequests <= 0)
        {
            return new ContextRequestValidationResult(
                Array.Empty<ContextRequest>(),
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["cap"] = requests.Count
                });
        }

        var matcher = BuildIgnoreMatcher(config.Ignore);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var accepted = new List<ContextRequest>();
        var drops = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var request in requests)
        {
            if (!IsSafeContextPath(request.Path))
            {
                IncrementDrop(drops, "invalid_path");
                continue;
            }

            if (!seen.Add(request.Path))
            {
                IncrementDrop(drops, "duplicate");
                continue;
            }

            if (matcher is not null && matcher.Match(request.Path).HasMatches)
            {
                IncrementDrop(drops, "ignored");
                continue;
            }

            if (LooksSecretLike(request.Path))
            {
                IncrementDrop(drops, "secret_path");
                continue;
            }

            if (accepted.Count >= config.Review.MaxContextRequests)
            {
                IncrementDrop(drops, "cap");
                continue;
            }

            accepted.Add(request);
        }

        return new ContextRequestValidationResult(accepted.ToArray(), drops);
    }

    private static Matcher? BuildIgnoreMatcher(IReadOnlyList<string> ignoreGlobs)
    {
        if (ignoreGlobs.Count == 0)
        {
            return null;
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        var hasPattern = false;
        foreach (var pattern in ignoreGlobs.Where(pattern => !string.IsNullOrWhiteSpace(pattern)))
        {
            matcher.AddInclude(pattern);
            hasPattern = true;
        }

        return hasPattern ? matcher : null;
    }

    private static bool IsSafeContextPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.StartsWith("/", StringComparison.Ordinal) ||
            path.Contains('\\'))
        {
            return false;
        }

        var segments = path.Split('/');
        return segments.All(segment =>
            !string.IsNullOrWhiteSpace(segment) &&
            !string.Equals(segment, ".", StringComparison.Ordinal) &&
            !string.Equals(segment, "..", StringComparison.Ordinal));
    }

    private static bool LooksSecretLike(string path)
    {
        var fileName = Path.GetFileName(path).ToLowerInvariant();
        var normalizedPath = path.ToLowerInvariant();

        return fileName is "id_rsa" or "id_dsa" or "id_ecdsa" or "id_ed25519" ||
            fileName.EndsWith(".pem", StringComparison.Ordinal) ||
            fileName.EndsWith(".key", StringComparison.Ordinal) ||
            fileName.EndsWith(".p12", StringComparison.Ordinal) ||
            fileName.EndsWith(".pfx", StringComparison.Ordinal) ||
            fileName.StartsWith(".env", StringComparison.Ordinal) ||
            normalizedPath.Contains("/.env", StringComparison.Ordinal);
    }

    private static void IncrementDrop(IDictionary<string, int> drops, string reason)
    {
        drops.TryGetValue(reason, out var count);
        drops[reason] = count + 1;
    }

    private void LogContextRequestDrops(
        IReadOnlyDictionary<string, int> drops,
        int requestedCount,
        int acceptedCount,
        ReviewJob job)
    {
        if (drops.Count == 0)
        {
            return;
        }

        logger.LogWarning(
            "Dropped {DroppedCount}/{RequestedCount} agentic context requests for {Owner}/{Repo}#{PrNumber}; accepted {AcceptedCount}. Reasons: {Reasons}",
            drops.Values.Sum(),
            requestedCount,
            job.Owner,
            job.Repo,
            job.PrNumber,
            acceptedCount,
            string.Join(", ", drops.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")));
    }

    private sealed record PatchBudgetResult(
        IReadOnlyList<FileChange> Files,
        IReadOnlyList<string> SkippedPaths);

    private sealed record FilePatchLineCount(
        FileChange File,
        long LineCount);

    private sealed record ContextRequestValidationResult(
        IReadOnlyList<ContextRequest> Requests,
        IReadOnlyDictionary<string, int> DropCounts);

    private sealed record SelfCritiqueRun(
        Task<IReadOnlyList<InlineComment>> Task,
        CancellationTokenSource Cancellation);
}
