using System.Diagnostics;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Options;
using Octokit;
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

    private readonly IReviewJobQueue queue;
    private readonly IInstallationTokenProvider tokenProvider;
    private readonly IPullRequestFetcher pullRequestFetcher;
    private readonly IRepoConfigFetcher repoConfigFetcher;
    private readonly IReviewLlmFactory llmFactory;
    private readonly IReviewPoster reviewPoster;
    private readonly IGroundingProvider groundingProvider;
    private readonly IPrReviewStateStore prReviewStateStore;
    private readonly ReviewBotMetrics metrics;
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
        // fetching config. For push/open triggers the SHA comes from the event payload, so we
        // defer the metadata call until after the cheap enabled/trigger checks.
        PullRequestMetadata? prefetchedMetadata = null;
        var configSha = job.HeadSha;
        if (configSha is null)
        {
            prefetchedMetadata = await pullRequestFetcher
                .FetchMetadataAsync(job.Owner, job.Repo, job.PrNumber, installationToken.Token, ct)
                .ConfigureAwait(false);
            configSha = prefetchedMetadata.HeadSha;
        }

        var config = await repoConfigFetcher
            .FetchAsync(job.Owner, job.Repo, configSha, installationToken.Token, ct)
            .ConfigureAwait(false);

        if (!config.Enabled)
        {
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
        var lastSha = await prReviewStateStore
            .GetLastShaAsync(job.InstallationId, repoFullName, job.PrNumber, ct)
            .ConfigureAwait(false);

        var metadata = prefetchedMetadata ?? await pullRequestFetcher
            .FetchMetadataAsync(job.Owner, job.Repo, job.PrNumber, installationToken.Token, ct)
            .ConfigureAwait(false);

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
        var groundingSw = Stopwatch.StartNew();
        var grounding = await groundingProvider.GetContextAsync(groundingRequest, ct).ConfigureAwait(false);
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

        var request = new ReviewRequest(
            metadata.Title,
            metadata.Body,
            metadata.BaseSha,
            metadata.HeadSha,
            files,
            config,
            grounding);

        var llm = llmFactory.Create(config.Model);
        var sw = Stopwatch.StartNew();
        var result = await llm.ReviewAsync(request, ct).ConfigureAwait(false);
        sw.Stop();
        logger.LogInformation(
            "LLM review completed in {LlmDurationMs}ms for {DeliveryId}",
            sw.Elapsed.TotalMilliseconds,
            job.DeliveryId);
        metrics.RecordLlmDuration(sw.Elapsed.TotalMilliseconds, config.Model.Provider);

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
        result = AppendFilesSkippedNote(result, patchBudgetResult.SkippedPaths);
        var candidateComments = FilterCandidateComments(result, config);
        candidateComments = await ApplySelfCritiqueAsync(llm, files, candidateComments, config, ct)
            .ConfigureAwait(false);
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
            var enrichedRaw = await llm.CompleteRawAsync(enrichedPayload, ct).ConfigureAwait(false);
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
        if (!config.Review.SelfCritique || candidateComments.Count == 0)
        {
            return candidateComments;
        }

        var highConfidence = candidateComments
            .Where(c => c.Confidence == Confidence.High)
            .ToArray();
        var critiqueCandidates = candidateComments
            .Where(c => c.Confidence != Confidence.High)
            .ToArray();

        if (critiqueCandidates.Length == 0)
        {
            return candidateComments;
        }

        var critiquePayload = SelfCritiquePromptBuilder.Build(files, critiqueCandidates);
        try
        {
            var critiqueSw = Stopwatch.StartNew();
            var rawCritique = await llm.CompleteRawAsync(critiquePayload, ct).ConfigureAwait(false);
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

    private static IReadOnlyList<InlineComment> FilterCandidateComments(ReviewResult result, ReviewConfig config)
    {
        if (!config.Review.InlineComments)
        {
            return Array.Empty<InlineComment>();
        }

        if (config.Review.MinConfidence == Confidence.Low)
        {
            return result.Comments;
        }

        return result.Comments
            .Where(c => c.Confidence >= config.Review.MinConfidence)
            .ToArray();
    }

    private static ReviewResult ApplyOutputConfig(
        ReviewResult result,
        IReadOnlyList<InlineComment> comments,
        ReviewConfig config)
    {
        var summary = config.Review.Summary ? result.Summary : string.Empty;

        return summary == result.Summary && ReferenceEquals(comments, result.Comments)
            ? result
            : new ReviewResult(summary, comments, result.ContextRequests);
    }

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
}
