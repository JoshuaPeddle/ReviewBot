using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Options;
using Octokit;
using ReviewBot.Api.Cost;
using ReviewBot.Api.Tracing;
using ReviewBot.Core.Context;
using ReviewBot.Core.Diff;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Jobs;
using ReviewBot.Core.Llm;
using ReviewBot.Core.Otel;
using ReviewBot.Core.Prompting;
using ReviewBot.Core.Verification;
using ReviewBot.Core.Storage;
using ReviewBot.GitHub.Auth;
using ReviewBot.GitHub.Config;
using ReviewBot.GitHub.Pulls;
using ReviewBot.Grounding;
using ReviewBot.Grounding.Diagnostics;
using ReviewBot.Grounding.Languages.DotNet;
using ReviewBot.Grounding.Workspace;
using ReviewBot.Retrieval;
using ReviewBot.Retrieval.Indexing;

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
    private readonly IModelContextRegistry modelContextRegistry;
    private readonly IReviewPromptTokenEstimator tokenEstimator;
    private readonly IRetrievalProvider retrievalProvider;
    private readonly IRepoIndexFactory repoIndexFactory;
    private readonly ISharedWorkspaceFactory sharedWorkspaceFactory;
    private readonly IReadOnlyList<IDiagnosticProvider> diagnosticProviders;
    private readonly IReviewCostCalculator costCalculator;
    private readonly IReviewTraceWriter traceWriter;
    private readonly TimeProvider clock;
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
        IReviewPromptTokenEstimator tokenEstimator,
        IRetrievalProvider retrievalProvider,
        IRepoIndexFactory repoIndexFactory,
        ISharedWorkspaceFactory sharedWorkspaceFactory,
        IEnumerable<IDiagnosticProvider> diagnosticProviders,
        IReviewCostCalculator costCalculator,
        IReviewTraceWriter traceWriter,
        TimeProvider clock,
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
        this.retrievalProvider = retrievalProvider ?? throw new ArgumentNullException(nameof(retrievalProvider));
        this.repoIndexFactory = repoIndexFactory ?? throw new ArgumentNullException(nameof(repoIndexFactory));
        this.sharedWorkspaceFactory = sharedWorkspaceFactory ?? throw new ArgumentNullException(nameof(sharedWorkspaceFactory));
        this.diagnosticProviders = (diagnosticProviders ?? throw new ArgumentNullException(nameof(diagnosticProviders))).ToArray();
        this.costCalculator = costCalculator ?? throw new ArgumentNullException(nameof(costCalculator));
        this.traceWriter = traceWriter ?? throw new ArgumentNullException(nameof(traceWriter));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
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
        var reviewStartTime = clock.GetUtcNow();
        using var reviewActivity = ReviewBotActivitySource.Instance.StartActivity("reviewbot.review");
        reviewActivity?.SetTag("review.owner", job.Owner);
        reviewActivity?.SetTag("review.repo", job.Repo);
        reviewActivity?.SetTag("review.pr_number", job.PrNumber);
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

        reviewActivity?.SetTag("review.model", config.Model.Name);
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

        // When the repo config omits a model name, resolve the provider's configured model now
        // (e.g. REVIEWBOT__OpenAi__ModelName) so token budgeting, cost, tracing, and the LLM call all
        // agree on the concrete model rather than an empty placeholder.
        if (string.IsNullOrWhiteSpace(config.Model.Name))
        {
            config = config with { Model = config.Model with { Name = llmFactory.ResolveModelName(config.Model) } };
            reviewActivity?.SetTag("review.model", config.Model.Name);
        }

        var repoFullName = $"{job.Owner}/{job.Repo}";
        var lastShaTask = prReviewStateStore
            .GetLastShaAsync(job.InstallationId, repoFullName, job.PrNumber, ct);
        var metadata = prefetchedMetadata ?? await metadataTask!.ConfigureAwait(false);
        reviewActivity?.SetTag("review.sha", metadata.HeadSha);
        var lastSha = await lastShaTask.ConfigureAwait(false);

        IReadOnlySet<string>? allowlist = null;
        IReadOnlySet<string>? changedPathsSinceLastReview = null;
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
                    changedPathsSinceLastReview = new HashSet<string>(compareResult.Paths, StringComparer.Ordinal);
                    allowlist = changedPathsSinceLastReview;
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
        var patchBudgetResult = config.Review.ChunkedReview
            ? new PatchBudgetResult(files, [])
            : ApplyPatchBudget(files, config.Review.MaxPatchLines, job);
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

        // One workspace per job: grounding clones it, retrieval indexing reuses the
        // same checkout, and it is disposed once when the job ends rather than each
        // stage cloning (and discarding) its own copy.
        await using var sharedWorkspace = sharedWorkspaceFactory.Create();

        var groundingRequest = new GroundingRequest(
            Owner: job.Owner,
            Repo: job.Repo,
            HeadSha: metadata.HeadSha,
            InstallationToken: installationToken.Token,
            Config: config.Grounding,
            HeadCloneUrl: metadata.HeadCloneUrl);
        var groundingSw = Stopwatch.StartNew();
        GroundingContext grounding;
        using (var _ = ReviewBotActivitySource.Instance.StartActivity("reviewbot.grounding"))
        {
            grounding = await GetGroundingContextAsync(groundingRequest, sharedWorkspace, job, ct).ConfigureAwait(false);
        }

        var groundingElapsed = groundingSw.Elapsed;
        // True only for genuine delta reviews, where the file set was restricted to
        // paths changed since the last review (fallbacks re-review the full list).
        var isIncrementalUpdate = changedPathsSinceLastReview is not null;
        var llm = llmFactory.Create(config.Model);
        var contextWindowTokens = await ResolveContextWindowTokensAsync(llm, config, ct).ConfigureAwait(false);
        var promptBudget = CreatePromptBudget(config, grounding, metadata, job, contextWindowTokens);
        var retrievalSw = Stopwatch.StartNew();
        RetrievalContextResult retrievalContext;
        using (var retrievalActivity = ReviewBotActivitySource.Instance.StartActivity("reviewbot.retrieval"))
        {
            retrievalContext = await GetRetrievalContextAsync(
                    files,
                    config,
                    promptBudget,
                    grounding,
                    metadata,
                    job,
                    installationToken.Token,
                    lastSha,
                    changedPathsSinceLastReview,
                    sharedWorkspace,
                    ct)
                .ConfigureAwait(false);
            retrievalActivity?.SetTag("retrieval.snippets_returned", retrievalContext.Snippets.Count);
            retrievalActivity?.SetTag("retrieval.symbols_queried", retrievalContext.SymbolsQueried);
            retrievalActivity?.SetTag("retrieval.bytes_used",
                retrievalContext.Snippets.Sum(s => Encoding.UTF8.GetByteCount(s.Content)));
        }

        var retrievalElapsed = retrievalSw.Elapsed;
        var repositoryContext = retrievalContext.Snippets;
        promptBudget = retrievalContext.Budget;
        var fullFileContextSw = Stopwatch.StartNew();
        var fullFileContext = await FetchFullFileContentsAsync(
                files,
                config,
                promptBudget,
                job,
                metadata.HeadSha,
                installationToken.Token,
                ct)
            .ConfigureAwait(false);
        var fullFileContextElapsed = fullFileContextSw.Elapsed;
        var fullFileContents = fullFileContext.Contents;
        promptBudget = fullFileContext.Budget;
        var reviewChunks = PlanReviewChunks(files, config, promptBudget, job);
        var selfCritiqueContext = new SelfCritiqueContext(repositoryContext, fullFileContents);
        var languageFacts = BuildLanguageFacts(files, fullFileContents, job);
        ReviewResult result;
        IReadOnlyList<InlineComment> candidateComments;
        IReadOnlyList<InlineComment> rawCandidateComments;
        IReadOnlyList<DroppedComment> droppedComments;
        IReadOnlyList<string> skippedPaths;
        IReadOnlyList<ChunkReviewOutcome>? chunkOutcomes = null;

        if (reviewChunks.Count > 1)
        {
            var reviewedFiles = GetReviewedChunkFiles(reviewChunks);
            LogPromptBudget(promptBudget, config, job);
            chunkOutcomes = await ReviewChunksAsync(
                    llm,
                    reviewChunks,
                    metadata,
                    config,
                    grounding,
                    repositoryContext,
                    fullFileContents,
                    job,
                    installationToken.Token,
                    promptBudget.ResponseReserveTokens,
                    isIncrementalUpdate,
                    languageFacts,
                    ct)
                .ConfigureAwait(false);
            result = ReviewResultMerger.Merge(chunkOutcomes.Select(o => o.Result).ToArray());
            rawCandidateComments = result.Comments;
            var filteredComments = FilterCandidateComments(result, config, grounding);
            var critiquedComments = await ApplySelfCritiqueWithDropsAsync(
                    llm, reviewedFiles, filteredComments.Comments, config, selfCritiqueContext, ct)
                .ConfigureAwait(false);
            candidateComments = critiquedComments.Comments;
            droppedComments = CombineDroppedComments(filteredComments.DroppedComments, critiquedComments.DroppedComments);
            skippedPaths = GetSkippedChunkPaths(files, reviewChunks, patchBudgetResult.SkippedPaths);
        }
        else
        {
            using var chunkActivity = ReviewBotActivitySource.Instance.StartActivity("reviewbot.chunk_review");
            chunkActivity?.SetTag("review.chunk_index", 1);
            chunkActivity?.SetTag("review.total_chunks", 1);

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
                fullFileContents,
                repositoryContext,
                MaxOutputTokens: promptBudget.ResponseReserveTokens,
                IsIncrementalUpdate: isIncrementalUpdate,
                LanguageFacts: languageFacts);

            var prompt = PromptBuilder.Build(request);
            ReviewResult singleChunkResult;
            var sw = Stopwatch.StartNew();
            using (var llmActivity = ReviewBotActivitySource.Instance.StartActivity("reviewbot.llm.review"))
            {
                singleChunkResult = await llm.ReviewAsync(request, ct).ConfigureAwait(false);
                if (singleChunkResult.TokenUsage is { } u)
                {
                    llmActivity?.SetTag("llm.prompt_tokens", u.PromptTokens);
                    llmActivity?.SetTag("llm.completion_tokens", u.CompletionTokens);
                }
            }

            sw.Stop();
            result = singleChunkResult;
            logger.LogInformation(
                "LLM review completed in {LlmDurationMs}ms for {DeliveryId}",
                sw.Elapsed.TotalMilliseconds,
                job.DeliveryId);
            metrics.RecordLlmDuration(sw.Elapsed.TotalMilliseconds, config.Model.Provider);

            var initialResult = result;
            var initialFilteredComments = FilterCandidateComments(initialResult, config, grounding);
            var speculativeSelfCritique = StartSelfCritiqueIfNeeded(
                llm, files, initialFilteredComments.Comments, config, selfCritiqueContext, ct);
            AgenticContextReviewOutcome agenticOutcome;

            try
            {
                agenticOutcome = await ApplyAgenticContextAsync(
                        llm,
                        request,
                        result,
                        config,
                        job,
                        metadata.HeadSha,
                        installationToken.Token,
                        ct)
                    .ConfigureAwait(false);
                result = agenticOutcome.Result;
            }
            catch
            {
                CancelSelfCritique(speculativeSelfCritique);
                throw;
            }

            skippedPaths = patchBudgetResult.SkippedPaths;
            if (ReferenceEquals(result, initialResult))
            {
                var critiqueResult = speculativeSelfCritique is null
                    ? new CommentFilterResult(initialFilteredComments.Comments, [])
                    : await AwaitSelfCritiqueAsync(speculativeSelfCritique).ConfigureAwait(false);
                rawCandidateComments = initialResult.Comments;
                candidateComments = critiqueResult.Comments;
                droppedComments = CombineDroppedComments(initialFilteredComments.DroppedComments, critiqueResult.DroppedComments);
            }
            else
            {
                CancelSelfCritique(speculativeSelfCritique);
                rawCandidateComments = result.Comments;
                var filteredComments = FilterCandidateComments(result, config, grounding);
                var critiquedComments = await ApplySelfCritiqueWithDropsAsync(
                        llm, files, filteredComments.Comments, config, selfCritiqueContext, ct)
                    .ConfigureAwait(false);
                candidateComments = critiquedComments.Comments;
                droppedComments = CombineDroppedComments(filteredComments.DroppedComments, critiquedComments.DroppedComments);
            }

            // Build a single-chunk outcome for tracing; carry the raw response from the initial call
            // since result may have been replaced by agentic context with a new summary.
            chunkOutcomes =
            [
                new ChunkReviewOutcome(
                    result with { RawLlmResponse = initialResult.RawLlmResponse },
                    prompt,
                    sw.Elapsed,
                    files,
                    agenticOutcome.Trace)
            ];
        }

        var verification = await ApplyVerificationAsync(
                candidateComments, grounding, config, files, sharedWorkspace, metadata, installationToken.Token, job, ct)
            .ConfigureAwait(false);
        candidateComments = verification.Comments;
        droppedComments = CombineDroppedComments(droppedComments, verification.RefutedDrops);
        if (config.Review.Summary)
        {
            // Keep the model's explanation, then state the facts about what actually
            // survived, so the counts can never disagree with the posted comments.
            result = result with
            {
                Summary = BuildFindingsSummary(
                    result.Summary,
                    candidateComments,
                    reviewedFileCount: chunkOutcomes?.Sum(o => o.ChunkFiles?.Count ?? 0) ?? files.Count,
                    reviewedChunkCount: chunkOutcomes?.Count ?? 1,
                    failedChunkCount: Math.Max(0, reviewChunks.Count - (chunkOutcomes?.Count ?? 1)))
            };
        }

        // ApplyOutputConfig may clear a no-issues summary (quiet on clean PRs); append the
        // skipped-files note and re-review hint afterward so they always survive.
        result = ApplyOutputConfig(result, candidateComments, config);
        if (config.Review.Summary)
        {
            result = AppendFilesSkippedNote(result, skippedPaths);
            result = AppendRereviewHint(result);
        }

        var reviewEvent = DetermineReviewEvent(result.Comments, config);
        metrics.RecordCommentsPosted(result.Comments.Count);

        decimal? estimatedCostUsd = null;
        if (result.TokenUsage is { } tokenUsage)
        {
            logger.LogInformation(
                "Review job {DeliveryId} for {Owner}/{Repo}#{PrNumber} used {PromptTokens} prompt tokens, {CompletionTokens} completion tokens ({CachedTokens} cached)",
                job.DeliveryId,
                job.Owner,
                job.Repo,
                job.PrNumber,
                tokenUsage.PromptTokens,
                tokenUsage.CompletionTokens,
                tokenUsage.CachedPromptTokens);

            estimatedCostUsd = costCalculator.ComputeCostUsd(config.Model.Name, tokenUsage);
            if (estimatedCostUsd is { } cost)
            {
                metrics.RecordCost((double)cost, config.Model.Provider, config.Model.Name);
                logger.LogInformation(
                    "Review job {DeliveryId} for {Owner}/{Repo}#{PrNumber} estimated cost ${EstimatedCostUsd:F6} USD",
                    job.DeliveryId,
                    job.Owner,
                    job.Repo,
                    job.PrNumber,
                    cost);
            }
        }

        using (var _ = ReviewBotActivitySource.Instance.StartActivity("reviewbot.post_review"))
        {
            await reviewPoster
                .PostAsync(job.Owner, job.Repo, job.PrNumber, metadata.HeadSha, result, files, installationToken.Token, ct, reviewEvent)
                .ConfigureAwait(false);
        }

        var timings = new TraceTimings
        {
            GroundingMs = groundingElapsed.TotalMilliseconds,
            RetrievalMs = retrievalElapsed.TotalMilliseconds,
            FullFileContextMs = fullFileContextElapsed.TotalMilliseconds,
            TotalMs = (clock.GetUtcNow() - reviewStartTime).TotalMilliseconds
        };

        await traceWriter
            .WriteAsync(BuildTrace(job, metadata, config, reviewStartTime, incrementalType, files, reviewChunks.Count, repositoryContext?.Count ?? 0, promptBudget, rawCandidateComments, droppedComments, result, chunkOutcomes, timings, estimatedCostUsd, traceWriter.IncludePrompts), ct)
            .ConfigureAwait(false);

        await prReviewStateStore
            .SetLastShaAsync(job.InstallationId, repoFullName, job.PrNumber, metadata.HeadSha, ct)
            .ConfigureAwait(false);
        metrics.RecordIncrementalReview(incrementalType);

        return JobProcessStatus.Success;
    }

    private sealed record ChunkReviewOutcome(
        ReviewResult Result,
        PromptPayload Prompt,
        TimeSpan Elapsed,
        IReadOnlyList<FileChange>? ChunkFiles = null,
        AgenticContextTraceData? AgenticContext = null);

    private sealed record AgenticContextReviewOutcome(
        ReviewResult Result,
        AgenticContextTraceData? Trace);

    private sealed record AgenticContextTraceData(
        IReadOnlyList<ContextRequest> Requested,
        IReadOnlyList<ContextRequest> Accepted,
        IReadOnlyList<string> FetchedPaths,
        IReadOnlyDictionary<string, int> DropCounts,
        bool SecondPassRan);

    private sealed record DroppedComment(InlineComment Comment, string Reason);

    private sealed record CommentFilterResult(
        IReadOnlyList<InlineComment> Comments,
        IReadOnlyList<DroppedComment> DroppedComments);

    private static ReviewTrace BuildTrace(
        ReviewJob job,
        PullRequestMetadata metadata,
        ReviewConfig config,
        DateTimeOffset startTime,
        string reviewType,
        IReadOnlyList<FileChange> filesReviewed,
        int chunkCount,
        int retrievalSnippetsCount,
        PromptBudget promptBudget,
        IReadOnlyList<InlineComment> rawCandidateComments,
        IReadOnlyList<DroppedComment> droppedComments,
        ReviewResult result,
        IReadOnlyList<ChunkReviewOutcome>? chunkOutcomes,
        TraceTimings timings,
        decimal? estimatedCostUsd,
        bool includePrompts)
    {
        return new ReviewTrace
        {
            DeliveryId = job.DeliveryId,
            TimestampUtc = startTime,
            Owner = job.Owner,
            Repo = job.Repo,
            PrNumber = job.PrNumber,
            HeadSha = metadata.HeadSha,
            BaseSha = metadata.BaseSha,
            PrTitle = metadata.Title,
            TriggerReason = job.Reason ?? string.Empty,
            ReviewType = reviewType,
            ModelProvider = config.Model.Provider,
            ModelName = config.Model.Name,
            FilesReviewed = filesReviewed.Select(f => f.Path).ToArray(),
            ChunkCount = chunkCount,
            RetrievalSnippetsCount = retrievalSnippetsCount,
            PromptBudget = new TraceBudgetSnapshot
            {
                ModelContextLimitTokens = promptBudget.ModelContextLimitTokens,
                SystemPromptTokens = promptBudget.SystemPromptTokens,
                GroundingTokens = promptBudget.GroundingTokens,
                ResponseReserveTokens = promptBudget.ResponseReserveTokens,
                ContentBudgetTokens = promptBudget.ContentBudgetTokens,
                ConsumedContentTokens = promptBudget.ConsumedContentTokens,
                RemainingContentTokens = promptBudget.RemainingContentTokens,
                ConsumedSections = promptBudget.ConsumedSections
                    .Select(s => new TraceBudgetSectionSnapshot { Name = s.Name, Tokens = s.Tokens })
                    .ToArray()
            },
            ResultSummary = result.Summary,
            CandidateComments = rawCandidateComments
                .Select(ToTraceComment)
                .ToArray(),
            DroppedComments = droppedComments
                .Select(c => ToTraceDroppedComment(c.Comment, c.Reason))
                .ToArray(),
            FinalComments = result.Comments
                .Select(ToTraceComment)
                .ToArray(),
            TokenUsage = result.TokenUsage is { } usage
                ? new TraceLlmTokenUsage
                {
                    PromptTokens = usage.PromptTokens,
                    CompletionTokens = usage.CompletionTokens,
                    CachedPromptTokens = usage.CachedPromptTokens
                }
                : null,
            EstimatedCostUsd = estimatedCostUsd,
            ChunkTraces = chunkOutcomes?.Select((o, i) => BuildTraceChunk(o, i, chunkOutcomes.Count, includePrompts)).ToArray(),
            Timings = timings
        };
    }

    private static TraceComment ToTraceComment(InlineComment comment) =>
        new()
        {
            Path = comment.Path,
            Line = comment.Line,
            Side = comment.Side,
            Body = comment.Body,
            Severity = comment.Severity.ToString().ToLowerInvariant(),
            Confidence = comment.Confidence.ToString().ToLowerInvariant(),
            Verification = comment.Verification == VerificationStatus.Verified ? "verified" : null
        };

    private static TraceDroppedComment ToTraceDroppedComment(InlineComment comment, string reason) =>
        new()
        {
            Path = comment.Path,
            Line = comment.Line,
            Side = comment.Side,
            Body = comment.Body,
            Severity = comment.Severity.ToString().ToLowerInvariant(),
            Confidence = comment.Confidence.ToString().ToLowerInvariant(),
            Reason = reason
        };

    private static TraceChunk BuildTraceChunk(ChunkReviewOutcome outcome, int index, int totalChunks, bool includePrompts)
    {
        var systemBytes = System.Text.Encoding.UTF8.GetByteCount(outcome.Prompt.SystemPrompt);
        var userBytes = System.Text.Encoding.UTF8.GetByteCount(outcome.Prompt.UserPrompt);
        var rawBytes = outcome.Result.RawLlmResponse is { } raw ? System.Text.Encoding.UTF8.GetByteCount(raw) : 0;
        var files = outcome.ChunkFiles is { Count: > 0 }
            ? outcome.ChunkFiles.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray()
            : outcome.Result.Comments.Select(c => c.Path).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        return new TraceChunk
        {
            ChunkIndex = index + 1,
            TotalChunks = totalChunks,
            Files = files,
            ElapsedMs = outcome.Elapsed.TotalMilliseconds,
            PromptSystemBytes = systemBytes,
            PromptUserBytes = userBytes,
            PromptSystem = includePrompts ? outcome.Prompt.SystemPrompt : null,
            PromptUser = includePrompts ? outcome.Prompt.UserPrompt : null,
            RawLlmResponseBytes = rawBytes,
            RawLlmResponse = includePrompts ? outcome.Result.RawLlmResponse : null,
            AgenticContext = outcome.AgenticContext is { } agenticContext
                ? new TraceAgenticContext
                {
                    Requested = agenticContext.Requested
                        .Select(request => new TraceContextRequest
                        {
                            Path = request.Path,
                            Reason = request.Reason
                        })
                        .ToArray(),
                    Accepted = agenticContext.Accepted
                        .Select(request => new TraceContextRequest
                        {
                            Path = request.Path,
                            Reason = request.Reason
                        })
                        .ToArray(),
                    FetchedPaths = agenticContext.FetchedPaths
                        .OrderBy(path => path, StringComparer.Ordinal)
                        .ToArray(),
                    DropCounts = agenticContext.DropCounts
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => new TraceDropCount
                        {
                            Reason = pair.Key,
                            Count = pair.Value
                        })
                        .ToArray(),
                    SecondPassRan = agenticContext.SecondPassRan
                }
                : null
        };
    }

    private enum JobProcessStatus { Success, Skipped }

    private sealed record FullFileContextResult(
        IReadOnlyDictionary<string, string>? Contents,
        PromptBudget Budget);

    private IReadOnlyList<ReviewChunk> PlanReviewChunks(
        IReadOnlyList<FileChange> files,
        ReviewConfig config,
        PromptBudget promptBudget,
        ReviewJob job)
    {
        var planner = new ReviewChunkPlanner(text => EstimateTokens(config, text));
        var estimatedDiffTokens = planner.EstimateDiffTokens(files, config.Review.MaxPatchLines);

        // Split once the diff passes the headroom fraction, not once it overflows the whole
        // budget. Chunking used to be a pure "does it fit?" test, with chunk_headroom read
        // only afterwards to size the pieces — so on a large context window the knob could
        // not be reached at all: the diff always fit, and a review that fits was never
        // split however small the operator asked chunks to be.
        //
        // Fitting is not the only reason to split. A prompt can fit and still be more than
        // the model will reason about: on this repo, three reviews spent an entire output
        // allowance thinking and returned nothing, from prompts well inside the window.
        // Making headroom the trigger gives that failure a knob, and it is what "headroom"
        // already implied — leave room, rather than fill the budget and split only on
        // overflow. At the 0.80 default this splits slightly earlier than before.
        var chunkTargetTokens = Math.Max(1, (int)Math.Floor(
            promptBudget.RemainingContentTokens * config.Review.ChunkHeadroom));
        if (!config.Review.ChunkedReview || estimatedDiffTokens <= chunkTargetTokens)
        {
            return [new ReviewChunk(1, 1, files, estimatedDiffTokens)];
        }

        logger.LogWarning(
            "Review job {DeliveryId} for {Owner}/{Repo}#{PrNumber} has estimated diff cost of {DiffTokens} token(s), exceeding the {ChunkTargetTokens}-token chunk target ({Headroom:P0} of the {RemainingTokens}-token remaining prompt budget) for model {ModelName}; splitting into chunks",
            job.DeliveryId,
            job.Owner,
            job.Repo,
            job.PrNumber,
            estimatedDiffTokens,
            chunkTargetTokens,
            config.Review.ChunkHeadroom,
            promptBudget.RemainingContentTokens,
            config.Model.Name);

        var chunks = planner.PlanChunks(
            files,
            promptBudget.RemainingContentTokens,
            config.Review.ChunkHeadroom,
            config.Review.MaxChunks,
            config.Review.MaxPatchLines);
        var reviewedFileCount = chunks.Sum(chunk => chunk.Files.Count);
        if (reviewedFileCount < files.Count)
        {
            logger.LogWarning(
                "Review job {DeliveryId} for {Owner}/{Repo}#{PrNumber} needed more than max_chunks {MaxChunks}; reviewing {ReviewedFileCount}/{FileCount} file(s)",
                job.DeliveryId,
                job.Owner,
                job.Repo,
                job.PrNumber,
                config.Review.MaxChunks,
                reviewedFileCount,
                files.Count);
        }

        logger.LogInformation(
            "Chunked review planned {ChunkCount} chunk(s) for {Owner}/{Repo}#{PrNumber}",
            chunks.Count,
            job.Owner,
            job.Repo,
            job.PrNumber);
        return chunks;
    }

    private async Task<IReadOnlyList<ChunkReviewOutcome>> ReviewChunksAsync(
        IReviewLlm llm,
        IReadOnlyList<ReviewChunk> chunks,
        PullRequestMetadata metadata,
        ReviewConfig config,
        GroundingContext grounding,
        IReadOnlyList<RepositoryContextSnippet>? repositoryContext,
        IReadOnlyDictionary<string, string>? fullFileContents,
        ReviewJob job,
        string installationToken,
        int maxOutputTokens,
        bool isIncrementalUpdate,
        IReadOnlyList<LanguageFact>? languageFacts,
        CancellationToken ct)
    {
        // One dispatch path for every provider: a gate of 1 is sequential, so the old
        // parallel/serial branch is just this with the degree fixed at either end.
        var degree = Math.Max(1, llm.MaxConcurrentRequests);
        logger.LogDebug(
            "Reviewing {ChunkCount} chunk(s) for {DeliveryId} at concurrency {Degree}",
            chunks.Count,
            job.DeliveryId,
            degree);

        ChunkReviewOutcome?[] outcomes;
        using (var gate = new SemaphoreSlim(degree, degree))
        {
            outcomes = await Task.WhenAll(chunks.Select(async chunk =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return await TryReviewChunkAsync(
                            llm,
                            chunk,
                            metadata,
                            config,
                            grounding,
                            repositoryContext,
                            fullFileContents,
                            job,
                            installationToken,
                            maxOutputTokens,
                            isIncrementalUpdate,
                            languageFacts,
                            ct)
                        .ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
        }

        var succeeded = outcomes.OfType<ChunkReviewOutcome>().ToArray();
        if (succeeded.Length == 0)
        {
            // Every chunk came back unusable, so there is no review to post. Fail loudly
            // rather than posting an empty one that reads as "no issues found".
            throw new LlmResponseUnusableException(
                $"All {chunks.Count} review chunk(s) returned an unusable response.");
        }

        if (succeeded.Length < outcomes.Length)
        {
            logger.LogWarning(
                "Review job {DeliveryId} for {Owner}/{Repo}#{PrNumber} lost {FailedChunks} of {TotalChunks} "
                + "chunk(s) to unusable responses; posting the findings from the {SucceededChunks} that worked",
                job.DeliveryId,
                job.Owner,
                job.Repo,
                job.PrNumber,
                outcomes.Length - succeeded.Length,
                outcomes.Length,
                succeeded.Length);
        }

        return succeeded;
    }

    /// <summary>
    /// Runs one chunk, returning null when the provider gave us nothing usable.
    /// </summary>
    /// <remarks>
    /// A single bad chunk should not cost the whole review: the other chunks' findings
    /// are still real and worth posting. Only a total wipeout fails the job, which
    /// <see cref="ReviewChunksAsync"/> checks for.
    /// </remarks>
    private async Task<ChunkReviewOutcome?> TryReviewChunkAsync(
        IReviewLlm llm,
        ReviewChunk chunk,
        PullRequestMetadata metadata,
        ReviewConfig config,
        GroundingContext grounding,
        IReadOnlyList<RepositoryContextSnippet>? repositoryContext,
        IReadOnlyDictionary<string, string>? fullFileContents,
        ReviewJob job,
        string installationToken,
        int maxOutputTokens,
        bool isIncrementalUpdate,
        IReadOnlyList<LanguageFact>? languageFacts,
        CancellationToken ct)
    {
        try
        {
            return await ReviewChunkAsync(
                    llm,
                    chunk,
                    metadata,
                    config,
                    grounding,
                    repositoryContext,
                    fullFileContents,
                    job,
                    installationToken,
                    maxOutputTokens,
                    isIncrementalUpdate,
                    languageFacts,
                    ct)
                .ConfigureAwait(false);
        }
        catch (LlmResponseUnusableException ex)
        {
            logger.LogWarning(
                ex,
                "Review chunk {ChunkIndex}/{TotalChunks} for {DeliveryId} returned an unusable response; "
                + "continuing with the remaining chunks",
                chunk.Index,
                chunk.TotalChunks,
                job.DeliveryId);
            return null;
        }
    }

    private async Task<ChunkReviewOutcome> ReviewChunkAsync(
        IReviewLlm llm,
        ReviewChunk chunk,
        PullRequestMetadata metadata,
        ReviewConfig config,
        GroundingContext grounding,
        IReadOnlyList<RepositoryContextSnippet>? repositoryContext,
        IReadOnlyDictionary<string, string>? fullFileContents,
        ReviewJob job,
        string installationToken,
        int maxOutputTokens,
        bool isIncrementalUpdate,
        IReadOnlyList<LanguageFact>? languageFacts,
        CancellationToken ct)
    {
        using var chunkActivity = ReviewBotActivitySource.Instance.StartActivity("reviewbot.chunk_review");
        chunkActivity?.SetTag("review.chunk_index", chunk.Index);
        chunkActivity?.SetTag("review.total_chunks", chunk.TotalChunks);

        var request = new ReviewRequest(
            metadata.Title,
            metadata.Body,
            metadata.BaseSha,
            metadata.HeadSha,
            chunk.Files,
            config,
            grounding,
            FilterFullFileContents(fullFileContents, chunk.Files),
            repositoryContext,
            ChunkIndex: chunk.Index,
            TotalChunks: chunk.TotalChunks,
            MaxOutputTokens: maxOutputTokens,
            IsIncrementalUpdate: isIncrementalUpdate,
            LanguageFacts: FilterLanguageFacts(languageFacts, chunk.Files));

        var prompt = PromptBuilder.Build(request);
        ReviewResult result;
        var sw = Stopwatch.StartNew();
        using (var llmActivity = ReviewBotActivitySource.Instance.StartActivity("reviewbot.llm.review"))
        {
            result = await llm.ReviewAsync(request, ct).ConfigureAwait(false);
            if (result.TokenUsage is { } u)
            {
                llmActivity?.SetTag("llm.prompt_tokens", u.PromptTokens);
                llmActivity?.SetTag("llm.completion_tokens", u.CompletionTokens);
            }
        }

        sw.Stop();
        logger.LogInformation(
            "LLM review chunk {ChunkIndex}/{TotalChunks} completed in {LlmDurationMs}ms for {DeliveryId}",
            chunk.Index,
            chunk.TotalChunks,
            sw.Elapsed.TotalMilliseconds,
            job.DeliveryId);
        metrics.RecordLlmDuration(sw.Elapsed.TotalMilliseconds, config.Model.Provider);

        var agenticOutcome = await ApplyAgenticContextAsync(
                llm,
                request,
                result,
                config,
                job,
                metadata.HeadSha,
                installationToken,
                ct)
            .ConfigureAwait(false);

        return new ChunkReviewOutcome(
            agenticOutcome.Result with { RawLlmResponse = result.RawLlmResponse },
            prompt,
            sw.Elapsed,
            chunk.Files,
            agenticOutcome.Trace);
    }

    private static IReadOnlyDictionary<string, string>? FilterFullFileContents(
        IReadOnlyDictionary<string, string>? fullFileContents,
        IReadOnlyList<FileChange> files)
    {
        if (fullFileContents is null || fullFileContents.Count == 0)
        {
            return null;
        }

        var paths = files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var filtered = fullFileContents
            .Where(entry => paths.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        return filtered.Count == 0 ? null : filtered;
    }

    private static IReadOnlyList<string> GetSkippedChunkPaths(
        IReadOnlyList<FileChange> files,
        IReadOnlyList<ReviewChunk> chunks,
        IReadOnlyList<string> skippedPaths)
    {
        var reviewedPaths = chunks
            .SelectMany(chunk => chunk.Files)
            .Select(file => file.Path)
            .ToHashSet(StringComparer.Ordinal);
        return skippedPaths
            .Concat(files
                .Where(file => !reviewedPaths.Contains(file.Path))
                .Select(file => file.Path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Compiler-settled facts about constructs in the changed C# files, for the prompt.
    /// </summary>
    /// <remarks>
    /// Uses content we already have rather than forcing a clone: the full file when it was
    /// fetched for context, otherwise the reconstructed content of an added file, whose
    /// patch is the whole file. A modified file with no full-file context yields nothing,
    /// which simply leaves behaviour as it was.
    /// </remarks>
    private IReadOnlyList<LanguageFact>? BuildLanguageFacts(
        IReadOnlyList<FileChange> files,
        IReadOnlyDictionary<string, string>? fullFileContents,
        ReviewJob job)
    {
        var facts = new List<LanguageFact>();
        foreach (var file in files.Where(file => file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var source = fullFileContents?.GetValueOrDefault(file.Path)
                ?? (file.Status == FileChangeStatus.Added
                    ? UnifiedDiffParser.TryReconstructAddedFileContent(file.Patch)
                    : null);
            if (source is null)
            {
                continue;
            }

            try
            {
                facts.AddRange(RoslynLiteralFactExtractor.Extract(file.Path, source, file.CommentableLines));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Language facts: could not analyse {Path}; skipping", file.Path);
            }
        }

        if (facts.Count == 0)
        {
            return null;
        }

        logger.LogInformation(
            "Language facts: stated {FactCount} compiler-settled fact(s) for {Owner}/{Repo}#{PrNumber}",
            facts.Count,
            job.Owner,
            job.Repo,
            job.PrNumber);
        return facts;
    }

    /// <summary>Narrows the facts to the files a chunk actually contains.</summary>
    private static IReadOnlyList<LanguageFact>? FilterLanguageFacts(
        IReadOnlyList<LanguageFact>? facts,
        IReadOnlyList<FileChange> chunkFiles)
    {
        if (facts is null || facts.Count == 0)
        {
            return null;
        }

        var paths = chunkFiles.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var scoped = facts.Where(fact => paths.Contains(fact.Path)).ToArray();
        return scoped.Length == 0 ? null : scoped;
    }

    private static IReadOnlyList<FileChange> GetReviewedChunkFiles(IReadOnlyList<ReviewChunk> chunks) =>
        chunks
            .SelectMany(chunk => chunk.Files)
            .ToArray();

    /// <summary>
    /// Builds the posted summary: the model's own prose, followed by a factual line
    /// derived from the findings that actually survived.
    /// </summary>
    /// <remarks>
    /// The prose used to be discarded outright in favour of the generated line, because a
    /// model summary can describe a comment that filtering later dropped, or carry a claim
    /// nothing checked. But that traded a wrong summary for a contentless one — the first
    /// thing a human reads on the PR became a comment count they can already see, while
    /// the model still spent reasoning and output tokens producing prose that was thrown
    /// away. Keeping both puts the explanation back and keeps the counts honest.
    ///
    /// When nothing survived, the prose is dropped rather than shown: it would be
    /// describing findings that are not on the page.
    /// </remarks>
    /// <param name="reviewedFileCount">
    /// Files in the chunks that actually produced a result — not the files that were
    /// planned. A chunk whose response was unusable is dropped so the rest of the review
    /// can still post, and counting its files here would credit the bot for reading code
    /// no model ever saw.
    /// </param>
    /// <param name="failedChunkCount">
    /// Chunks that returned nothing usable. Any value above zero means the review is
    /// partial, which has to be said out loud: "no actionable issues were found" over a
    /// half-failed review reads as a clean bill of health for files nobody reviewed.
    /// </param>
    private static string BuildFindingsSummary(
        string? modelSummary,
        IReadOnlyList<InlineComment> comments,
        int reviewedFileCount,
        int reviewedChunkCount,
        int failedChunkCount)
    {
        var prefix = reviewedChunkCount > 1
            ? $"Reviewed {reviewedFileCount} file(s) across {reviewedChunkCount} chunk(s)."
            : $"Reviewed {reviewedFileCount} file(s).";

        var partialNote = failedChunkCount == 0
            ? string.Empty
            : $" ⚠️ {failedChunkCount} of {reviewedChunkCount + failedChunkCount} chunk(s) could not be reviewed"
                + " (the model returned no usable response), so this review is incomplete —"
                + " re-run it with `/review` before relying on the result.";

        if (comments.Count == 0)
        {
            return failedChunkCount == 0
                ? $"{prefix} No actionable issues were found."
                : $"{prefix} No actionable issues were found in the part that was reviewed.{partialNote}";
        }

        var highestSeverity = comments.Max(comment => comment.Severity).ToString().ToLowerInvariant();
        var issueText = comments.Count == 1 ? "1 actionable issue" : $"{comments.Count} actionable issues";
        var affectedPaths = comments
            .Select(comment => comment.Path)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(5)
            .Select(path => $"`{path}`")
            .ToArray();
        var pathText = affectedPaths.Length == 0 ? string.Empty : $" Most affected files: {string.Join(", ", affectedPaths)}.";
        var verifiedCount = comments.Count(comment => comment.Verification == VerificationStatus.Verified);
        var verifiedText = verifiedCount == 0 ? string.Empty : $" {verifiedCount} corroborated against ground truth.";
        var facts = $"{prefix} Found {issueText}; highest severity: {highestSeverity}.{pathText}{verifiedText}{partialNote}";

        return string.IsNullOrWhiteSpace(modelSummary)
            ? facts
            : $"{modelSummary.Trim()}\n\n{facts}";
    }

    private SelfCritiqueRun? StartSelfCritiqueIfNeeded(
        IReviewLlm llm,
        IReadOnlyList<FileChange> files,
        IReadOnlyList<InlineComment> candidateComments,
        ReviewConfig config,
        SelfCritiqueContext context,
        CancellationToken ct)
    {
        if (!ShouldRunSelfCritique(candidateComments, config))
        {
            return null;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = ApplySelfCritiqueWithDropsAsync(llm, files, candidateComments, config, context, cts.Token);
        return new SelfCritiqueRun(task, cts);
    }

    private static async Task<CommentFilterResult> AwaitSelfCritiqueAsync(SelfCritiqueRun run)
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

    private static IReadOnlyList<DroppedComment> CombineDroppedComments(
        IReadOnlyList<DroppedComment> first,
        IReadOnlyList<DroppedComment> second)
    {
        if (first.Count == 0)
        {
            return second;
        }

        if (second.Count == 0)
        {
            return first;
        }

        return first.Concat(second).ToArray();
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
        ISharedWorkspace sharedWorkspace,
        ReviewJob job,
        CancellationToken ct)
    {
        var groundingSw = Stopwatch.StartNew();
        var grounding = await groundingProvider.GetContextAsync(request, ct, sharedWorkspace).ConfigureAwait(false);
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

    private async Task<RetrievalContextResult> GetRetrievalContextAsync(
        IReadOnlyList<FileChange> files,
        ReviewConfig config,
        PromptBudget promptBudget,
        GroundingContext grounding,
        PullRequestMetadata metadata,
        ReviewJob job,
        string installationToken,
        string? lastIndexedSha,
        IReadOnlySet<string>? changedPathsSinceLastReview,
        ISharedWorkspace sharedWorkspace,
        CancellationToken ct)
    {
        if (!config.Retrieval.Enabled)
        {
            return new RetrievalContextResult([], promptBudget);
        }

        var indexReady = await EnsureRepositoryIndexedAsync(
                config,
                metadata,
                job,
                installationToken,
                lastIndexedSha,
                changedPathsSinceLastReview,
                sharedWorkspace,
                ct)
            .ConfigureAwait(false);
        if (!indexReady)
        {
            return new RetrievalContextResult([], promptBudget);
        }

        var request = new ReviewRequest(
            metadata.Title,
            metadata.Body,
            metadata.BaseSha,
            metadata.HeadSha,
            files,
            config,
            grounding);

        try
        {
            var retrieval = await retrievalProvider
                .GetContextAsync(job.Owner, job.Repo, request, promptBudget, ct)
                .ConfigureAwait(false);
            if (retrieval.Snippets.Count > 0)
            {
                logger.LogInformation(
                    "Retrieval context: included {SnippetCount} snippet(s) for {Owner}/{Repo}#{PrNumber}",
                    retrieval.Snippets.Count,
                    job.Owner,
                    job.Repo,
                    job.PrNumber);
            }

            return retrieval;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Retrieval context lookup failed; continuing without repository snippets");
            return new RetrievalContextResult([], promptBudget);
        }
    }

    private async Task<bool> EnsureRepositoryIndexedAsync(
        ReviewConfig config,
        PullRequestMetadata metadata,
        ReviewJob job,
        string installationToken,
        string? lastIndexedSha,
        IReadOnlySet<string>? changedPathsSinceLastReview,
        ISharedWorkspace sharedWorkspace,
        CancellationToken ct)
    {
        var repoIndex = repoIndexFactory.Create(config.Retrieval.IndexCacheDir);
        var key = new RepoIndexKey(job.Owner, job.Repo, metadata.HeadSha);
        if (await repoIndex.IsIndexedAsync(key, ct).ConfigureAwait(false))
        {
            return true;
        }

        var cloneUrl = string.IsNullOrWhiteSpace(metadata.HeadCloneUrl)
            ? $"https://github.com/{job.Owner}/{job.Repo}.git"
            : metadata.HeadCloneUrl;

        using var indexActivity = ReviewBotActivitySource.Instance.StartActivity("reviewbot.retrieval.index_sha");
        indexActivity?.SetTag("review.owner", job.Owner);
        indexActivity?.SetTag("review.repo", job.Repo);
        indexActivity?.SetTag("review.sha", metadata.HeadSha);

        try
        {
            logger.LogInformation(
                "Retrieval index: indexing {Owner}/{Repo}@{HeadSha} before reviewing PR #{PrNumber}",
                job.Owner,
                job.Repo,
                metadata.HeadSha,
                job.PrNumber);

            // Reuse the job-scoped checkout (grounding may have already cloned it);
            // the scope owns disposal, so this method must not dispose the workspace.
            var workspace = await sharedWorkspace
                .GetOrCreateAsync(new WorkspaceRequest(cloneUrl, metadata.HeadSha, installationToken), ct)
                .ConfigureAwait(false);
            var request = new RepoIndexRequest(job.Owner, job.Repo, metadata.HeadSha, workspace.LocalPath, config.Ignore);
            if (lastIndexedSha is not null &&
                changedPathsSinceLastReview is { Count: > 0 } &&
                CanUseIncrementalRetrievalIndex(changedPathsSinceLastReview) &&
                await repoIndex.IsIndexedAsync(new RepoIndexKey(job.Owner, job.Repo, lastIndexedSha), ct).ConfigureAwait(false))
            {
                indexActivity?.SetTag("retrieval.index_mode", "incremental");
                indexActivity?.SetTag("retrieval.changed_paths", changedPathsSinceLastReview.Count);
                logger.LogInformation(
                    "Retrieval index: incrementally indexing {Owner}/{Repo}@{HeadSha} from {BaseSha} with {ChangedPathCount} changed path(s)",
                    job.Owner,
                    job.Repo,
                    metadata.HeadSha,
                    lastIndexedSha,
                    changedPathsSinceLastReview.Count);
                await repoIndex
                    .IndexChangesAsync(
                        request,
                        new RepoIndexKey(job.Owner, job.Repo, lastIndexedSha),
                        changedPathsSinceLastReview,
                        ct)
                    .ConfigureAwait(false);
            }
            else
            {
                indexActivity?.SetTag("retrieval.index_mode", "full");
                await repoIndex
                    .IndexAsync(request, ct)
                    .ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Retrieval index update failed; continuing without repository snippets");
            return false;
        }
    }

    private static bool CanUseIncrementalRetrievalIndex(IReadOnlySet<string> changedPaths) =>
        !changedPaths.Contains(".github/review-bot.yml") &&
        !changedPaths.Contains(".github/review-bot.yaml");

    /// <summary>
    /// Resolves the model's context window, preferring a value probed from the
    /// live provider (e.g. vLLM <c>max_model_len</c>) over the name-based static
    /// registry. Probing never throws; on any miss we fall back to the registry.
    /// </summary>
    private async Task<int> ResolveContextWindowTokensAsync(IReviewLlm llm, ReviewConfig config, CancellationToken ct)
    {
        if (llm is IModelContextProbe probe)
        {
            var probed = await probe.TryGetContextWindowTokensAsync(config.Model.Name, ct).ConfigureAwait(false);
            if (probed is > 0)
            {
                return probed.Value;
            }
        }

        return modelContextRegistry.GetContextWindowTokens(config.Model.Name);
    }

    private PromptBudget CreatePromptBudget(
        ReviewConfig config,
        GroundingContext grounding,
        PullRequestMetadata metadata,
        ReviewJob job,
        int contextWindowTokens)
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
        var systemPromptTokens = EstimateTokens(config, baseSystemPrompt);
        var groundingTokens = Math.Max(
            0,
            EstimateTokens(config, groundedSystemPrompt) - systemPromptTokens);

        // Scale the response reserve to the detected window so a fixed reserve
        // can't starve the prompt on a small-context model (no-op at 32K+).
        var responseReserveTokens = ContextBudget.ResolveResponseReserveTokens(
            config.Review.ResponseReserveTokens,
            contextWindowTokens);
        if (responseReserveTokens != config.Review.ResponseReserveTokens)
        {
            logger.LogInformation(
                "Clamped response reserve from {ConfiguredReserve} to {EffectiveReserve} token(s) to fit the {ContextTokens}-token context window for {Owner}/{Repo}#{PrNumber}",
                config.Review.ResponseReserveTokens,
                responseReserveTokens,
                contextWindowTokens,
                job.Owner,
                job.Repo,
                job.PrNumber);
        }

        var budget = PromptBudget.Create(
            contextWindowTokens,
            systemPromptTokens,
            groundingTokens,
            responseReserveTokens);

        var metadataTokens = EstimateTokens(config, metadata.Title) +
            EstimateTokens(config, metadata.Body);
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
        var diffTokens = EstimateDiffTokens(files, config, config.Review.MaxPatchLines);
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

    private int EstimateDiffTokens(IReadOnlyList<FileChange> files, ReviewConfig config, int maxPatchLines)
    {
        var tokens = 0;
        foreach (var file in files)
        {
            tokens += EstimateTokens(config,
                $"{file.Path} {file.Status} +{file.AdditionsCount} -{file.DeletionsCount}\n{TakePatchLines(file.Patch, maxPatchLines)}");
        }

        return tokens;
    }

    private int EstimateTokens(ReviewConfig config, string? text) =>
        tokenEstimator.EstimateTokens(config.Model, text);

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

        // Nothing left to spend, so there is nothing worth fetching.
        if (budget.RemainingContentTokens == 0)
        {
            logger.LogDebug(
                "Full-file context skipped for {Owner}/{Repo}#{PrNumber}: no content budget remains",
                job.Owner,
                job.Repo,
                job.PrNumber);
            return new FullFileContextResult(null, budget);
        }

        // Every candidate is requested. There used to be a pre-fetch budget gate here that
        // charged each file its *patch* size — a quantity with no relation to the file size
        // it was guarding, since a five-line patch on a two-thousand-line file estimates ~20
        // tokens against a real cost of ~15,000. It therefore admitted everything and only
        // looked like a control. GitHub's files API reports additions, deletions and the
        // patch but not the file's size, so nothing better is knowable until the content is
        // in hand; the real gate is below, where the sizes are exact.
        var selectedRequests = FullFileContextSelector
            .SelectCandidates(files, config.Review.FullFileMaxBytes)
            .Select(file => new ContextRequest(file.Path, "full-file context for small changed file"))
            .ToList();

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

        // Smallest first. The sizes are exact now, and a budget that cannot hold everything
        // should buy as many files as it can rather than however many happened to come
        // before the large one: in candidate order a single 13K-token file can consume what
        // five 2K-token files would have fitted in. Never includes fewer files than the old
        // order, and usually more.
        foreach (var fetchedFile in fetchedFiles
            .Select(file => (file.Path, file.Content, Tokens: EstimateTokens(config, file.Content)))
            .OrderBy(file => file.Tokens)
            .ThenBy(file => file.Path, StringComparer.Ordinal))
        {
            var estimatedTokens = fetchedFile.Tokens;
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

        var note = "files_skipped: The following files were omitted from automated review because the pull request exceeded the configured review budget: "
            + string.Join(", ", skippedPaths.Select(path => $"`{path}`"))
            + ".";
        var summary = string.IsNullOrWhiteSpace(result.Summary)
            ? note
            : $"{result.Summary.TrimEnd()}\n\n{note}";

        return result with { Summary = summary };
    }

    private static ReviewResult AppendRereviewHint(ReviewResult result)
    {
        const string hint = "*To re-request a review, comment `/review`.*";
        var summary = string.IsNullOrWhiteSpace(result.Summary)
            ? hint
            : $"{result.Summary.TrimEnd()}\n\n---\n{hint}";
        return result with { Summary = summary };
    }

    private async Task<AgenticContextReviewOutcome> ApplyAgenticContextAsync(
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
            return new AgenticContextReviewOutcome(initialResult, null);
        }

        var validation = FilterContextRequests(initialResult.ContextRequests, config);
        LogContextRequestDrops(validation.DropCounts, initialResult.ContextRequests.Count, validation.Requests.Count, job);
        var trace = new AgenticContextTraceData(
            initialResult.ContextRequests.ToArray(),
            validation.Requests.ToArray(),
            Array.Empty<string>(),
            new Dictionary<string, int>(validation.DropCounts, StringComparer.Ordinal),
            SecondPassRan: false);

        if (validation.Requests.Count == 0)
        {
            return new AgenticContextReviewOutcome(initialResult, trace);
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
            return new AgenticContextReviewOutcome(initialResult, trace);
        }

        trace = trace with
        {
            FetchedPaths = fetchedFiles
                .Select(file => file.Path)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };

        if (fetchedFiles.Count == 0)
        {
            logger.LogInformation(
                "Agentic context: requested {RequestCount} file(s) for {Owner}/{Repo}#{PrNumber} but none could be fetched (404, binary, or oversized); using initial comments",
                validation.Requests.Count,
                job.Owner,
                job.Repo,
                job.PrNumber);
            return new AgenticContextReviewOutcome(initialResult, trace);
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
            trace = trace with { SecondPassRan = true };
            if (enrichedParsed.Success)
            {
                logger.LogInformation(
                    "Agentic context: second-pass review completed for {Owner}/{Repo}#{PrNumber}; {CommentCount} comment(s) in final result",
                    job.Owner,
                    job.Repo,
                    job.PrNumber,
                    enrichedParsed.Value!.Comments.Count);
                return new AgenticContextReviewOutcome(
                    enrichedParsed.Value! with { TokenUsage = initialResult.TokenUsage },
                    trace);
            }

            logger.LogWarning(
                "Agentic context second-pass response was invalid: {Error}; using initial comments",
                enrichedParsed.Error);
            return new AgenticContextReviewOutcome(initialResult, trace);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Agentic context second pass failed; using initial comments");
            return new AgenticContextReviewOutcome(initialResult, trace with { SecondPassRan = true });
        }
    }

    private async Task<IReadOnlyList<InlineComment>> ApplySelfCritiqueAsync(
        IReviewLlm llm,
        IReadOnlyList<FileChange> files,
        IReadOnlyList<InlineComment> candidateComments,
        ReviewConfig config,
        SelfCritiqueContext context,
        CancellationToken ct)
    {
        if (!ShouldRunSelfCritique(candidateComments, config))
        {
            return candidateComments;
        }

        var critiqueCandidates = candidateComments.ToArray();

        // The critic is handed the same evidence the review pass saw. Given only the diff
        // it cannot distinguish a finding grounded in a retrieved definition from one
        // invented about absent code, and deletes both.
        var critiquePayload = SelfCritiquePromptBuilder.Build(
            files,
            critiqueCandidates,
            context.RepositoryContext,
            context.FullFileContents,
            config.Review.MaxPatchLines);
        try
        {
            var critiqueSw = Stopwatch.StartNew();
            string rawCritique;
            using (var _ = ReviewBotActivitySource.Instance.StartActivity("reviewbot.llm.self_critique"))
            {
                rawCritique = await llm.CompleteRawAsync(critiquePayload, ct, "self_critique").ConfigureAwait(false);
            }

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
                "Self-critique retained {Retained}/{Total} comments. Rationale: {Rationale}",
                retained.Length,
                critiqueCandidates.Length,
                critique.Rationale);

            return retained;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Self-critique failed; using full initial comment set");
            return candidateComments;
        }
    }

    private async Task<CommentFilterResult> ApplySelfCritiqueWithDropsAsync(
        IReviewLlm llm,
        IReadOnlyList<FileChange> files,
        IReadOnlyList<InlineComment> candidateComments,
        ReviewConfig config,
        SelfCritiqueContext context,
        CancellationToken ct)
    {
        var retained = await ApplySelfCritiqueAsync(llm, files, candidateComments, config, context, ct)
            .ConfigureAwait(false);
        return new CommentFilterResult(
            retained,
            FindDroppedComments(candidateComments, retained, "self_critique"));
    }

    /// <summary>
    /// The non-diff evidence the review pass was given, forwarded to the critique pass.
    /// </summary>
    private sealed record SelfCritiqueContext(
        IReadOnlyList<RepositoryContextSnippet>? RepositoryContext,
        IReadOnlyDictionary<string, string>? FullFileContents);

    // Every candidate goes through critique. The old routing exempted high-confidence
    // non-error comments that matched no checkable-claim phrase; two thirds of that test
    // is now gone with the phrase lists, and the remaining third rests on a signal the
    // model does not actually vary — it reported high confidence on 14 of 14 comments in
    // the reference run — so the exemption amounted to "skip review for warnings".
    //
    // The cost is real and deliberate: a review that produces any comment now always pays
    // a second LLM round-trip, where before an all-high-confidence, sub-error review could
    // skip it. That is only worth paying if the critique earns its keep, which is exactly
    // what the live A/B measures; if it does not, the answer is to turn the stage off
    // rather than to exempt comments from it on an uninformative signal.
    private static bool ShouldRunSelfCritique(IReadOnlyList<InlineComment> candidateComments, ReviewConfig config) =>
        config.Review.SelfCritique && candidateComments.Count > 0;

    private static CommentFilterResult FilterCandidateComments(
        ReviewResult result,
        ReviewConfig config,
        GroundingContext? grounding)
    {
        if (!config.Review.InlineComments)
        {
            return new CommentFilterResult(
                [],
                result.Comments
                    .Select(comment => new DroppedComment(comment, "inline_comments_disabled"))
                    .ToArray());
        }

        var kept = new List<InlineComment>(result.Comments.Count);
        var dropped = new List<DroppedComment>();

        foreach (var comment in result.Comments)
        {
            var reason = GetCommentDropReason(comment, config, grounding);
            if (reason is null)
            {
                kept.Add(comment);
                continue;
            }

            dropped.Add(new DroppedComment(comment, reason));
        }

        return new CommentFilterResult(kept.ToArray(), dropped.ToArray());
    }

    /// <summary>
    /// Deterministic, evidence-backed reasons to drop a candidate comment.
    /// </summary>
    /// <remarks>
    /// This deliberately holds only checks backed by something other than the comment's
    /// own wording. Six prose-matching filters (praise, confirmation, meta-review,
    /// non-actionable-process, speculative-missing-context, and the checkable-claim
    /// routing) used to live here, matching ~250 lines of English phrases against the
    /// body. Replayed against realistic true positives, six of seven were dropped: a
    /// pagination-truncation bug as "confirmation_only" because it said "this ensures",
    /// a concurrent-dictionary mutation as "non_actionable_process" because it said
    /// "consider whether", a mid-codepoint truncation bug as "praise_only" because it
    /// contained the word "correctly". None of it was measurable — the eval harness
    /// never ran this method — and every rule it enforced is already stated in the
    /// system prompt and the self-critique prompt, where a model can weigh it in
    /// context instead of matching a substring.
    /// </remarks>
    private static string? GetCommentDropReason(InlineComment comment, ReviewConfig config, GroundingContext? grounding)
    {
        if (comment.Confidence < config.Review.MinConfidence)
        {
            return "below_min_confidence";
        }

        // A successful Tier-2 build proves the code compiles, so a comment that
        // asserts a compile/syntax failure is provably wrong — drop it.
        if (ClaimsCompileFailureContradictedByBuild(comment.Body, grounding))
        {
            return "grounding_build_contradicts";
        }

        return null;
    }

    private static IReadOnlyList<DroppedComment> FindDroppedComments(
        IReadOnlyList<InlineComment> original,
        IReadOnlyList<InlineComment> retained,
        string reason)
    {
        if (original.Count == retained.Count)
        {
            return [];
        }

        var unmatchedRetained = retained.ToList();
        var dropped = new List<DroppedComment>();

        foreach (var comment in original)
        {
            var retainedIndex = unmatchedRetained.FindIndex(c => c == comment);
            if (retainedIndex >= 0)
            {
                unmatchedRetained.RemoveAt(retainedIndex);
                continue;
            }

            dropped.Add(new DroppedComment(comment, reason));
        }

        return dropped.ToArray();
    }

    // Verifies findings against ground truth before posting: refutes compile/syntax
    // claims on files an analyzer proved parse cleanly (dropping them), then marks
    // findings an analyzer independently corroborates as Verified. A no-op unless
    // build grounding or a diagnostic provider produced ground truth.
    private async Task<VerificationOutcome> ApplyVerificationAsync(
        IReadOnlyList<InlineComment> comments,
        GroundingContext grounding,
        ReviewConfig config,
        IReadOnlyList<FileChange> files,
        ISharedWorkspace sharedWorkspace,
        PullRequestMetadata metadata,
        string installationToken,
        ReviewJob job,
        CancellationToken ct)
    {
        if (!config.Review.Verification.Enabled || comments.Count == 0)
        {
            return new VerificationOutcome(comments, []);
        }

        var gathered = await GatherDiagnosticsAsync(
                grounding, config, files, sharedWorkspace, metadata, installationToken, job, ct)
            .ConfigureAwait(false);

        // Refute compile/syntax-failure claims on files an analyzer proved parse cleanly.
        var refutation = FindingRefuter.Refute(comments, gathered.CleanlyParsedPaths);
        var refutedDrops = refutation.Refuted.Count == 0
            ? Array.Empty<DroppedComment>()
            : refutation.Refuted.Select(c => new DroppedComment(c, "verification_parse_contradicts")).ToArray();
        if (refutation.Refuted.Count > 0)
        {
            logger.LogInformation(
                "Verification: refuted {RefutedCount} compile/syntax claim(s) on cleanly-parsed file(s) for {Owner}/{Repo}#{PrNumber}",
                refutation.Refuted.Count,
                job.Owner,
                job.Repo,
                job.PrNumber);
        }

        // Then refute language-semantics claims the syntax tree disproves. Separate from
        // the parse-based refutation above because these comments concede the code
        // compiles and assert it behaves differently, so parse results cannot touch them.
        var semantic = SemanticFindingRefuter.Refute(
            refutation.Kept,
            RoslynSemanticClaimVerifier.Verify,
            path => TryReadWorkspaceFile(gathered.WorkspacePath, path));
        if (semantic.Refuted.Count > 0)
        {
            refutedDrops = refutedDrops
                .Concat(semantic.Refuted.Select(c => new DroppedComment(c, "verification_semantics_contradict")))
                .ToArray();
            logger.LogInformation(
                "Verification: refuted {RefutedCount} language-semantics claim(s) the syntax tree disproves for {Owner}/{Repo}#{PrNumber}",
                semantic.Refuted.Count,
                job.Owner,
                job.Repo,
                job.PrNumber);
        }

        // Corroborate the survivors against diagnostics.
        var survivors = semantic.Kept;
        var finalComments = survivors;
        if (gathered.Diagnostics.Count > 0)
        {
            var corroborated = FindingCorroborator.Corroborate(survivors, gathered.Diagnostics);
            var verifiedCount = corroborated.Count(c => c.Evidence is not null);
            if (verifiedCount > 0)
            {
                logger.LogInformation(
                    "Verification: corroborated {VerifiedCount}/{CommentCount} finding(s) against diagnostics for {Owner}/{Repo}#{PrNumber}",
                    verifiedCount,
                    survivors.Count,
                    job.Owner,
                    job.Repo,
                    job.PrNumber);
                finalComments = corroborated
                    .Select(c => c.Evidence is { } evidence
                        ? AppendVerificationEvidence(c.Comment, evidence)
                        : c.Comment)
                    .ToArray();
            }
        }

        return new VerificationOutcome(finalComments, refutedDrops);
    }

    // Combines build diagnostics (when build grounding ran) with cheap, build-free
    // diagnostics from language providers (e.g. ruff) run against the existing checkout,
    // and records which files an analyzer proved parse cleanly (for refutation).
    private async Task<GatheredDiagnostics> GatherDiagnosticsAsync(
        GroundingContext grounding,
        ReviewConfig config,
        IReadOnlyList<FileChange> files,
        ISharedWorkspace sharedWorkspace,
        PullRequestMetadata metadata,
        string installationToken,
        ReviewJob job,
        CancellationToken ct)
    {
        var diagnostics = new List<Diagnostic>();
        if (grounding.Build?.Diagnostics is { Count: > 0 } buildDiagnostics)
        {
            diagnostics.AddRange(buildDiagnostics);
        }

        var providerResult = await RunDiagnosticProvidersAsync(
                grounding, config, files, sharedWorkspace, metadata, installationToken, job, ct)
            .ConfigureAwait(false);
        diagnostics.AddRange(providerResult.Diagnostics);
        return new GatheredDiagnostics(diagnostics, providerResult.CleanlyParsedPaths, providerResult.WorkspacePath);
    }

    private async Task<GatheredDiagnostics> RunDiagnosticProvidersAsync(
        GroundingContext grounding,
        ReviewConfig config,
        IReadOnlyList<FileChange> files,
        ISharedWorkspace sharedWorkspace,
        PullRequestMetadata metadata,
        string installationToken,
        ReviewJob job,
        CancellationToken ct)
    {
        var languageId = grounding.Language?.LanguageId;
        if (languageId is null || diagnosticProviders.Count == 0)
        {
            return GatheredDiagnostics.Empty;
        }

        var providers = diagnosticProviders
            .Where(p => string.Equals(p.LanguageId, languageId, StringComparison.Ordinal))
            .ToArray();
        if (providers.Length == 0)
        {
            return GatheredDiagnostics.Empty;
        }

        // Reuse the checkout retrieval indexing or build grounding already made; never
        // force a clone purely for verification.
        var cloneExists = config.Retrieval.Enabled || (config.Grounding.Enabled && config.Grounding.Build);
        if (!cloneExists)
        {
            return GatheredDiagnostics.Empty;
        }

        string workspacePath;
        try
        {
            var cloneUrl = string.IsNullOrWhiteSpace(metadata.HeadCloneUrl)
                ? $"https://github.com/{job.Owner}/{job.Repo}.git"
                : metadata.HeadCloneUrl;
            var workspace = await sharedWorkspace
                .GetOrCreateAsync(new WorkspaceRequest(cloneUrl, metadata.HeadSha, installationToken), ct)
                .ConfigureAwait(false);
            workspacePath = workspace.LocalPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Verification: workspace unavailable for diagnostic providers; skipping");
            return GatheredDiagnostics.Empty;
        }

        var changedPaths = files.Select(f => f.Path).ToArray();
        var results = new List<Diagnostic>();
        var cleanlyParsed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            try
            {
                using var _ = ReviewBotActivitySource.Instance.StartActivity("reviewbot.verification.diagnostics");
                var report = await provider.GetDiagnosticsAsync(workspacePath, changedPaths, ct).ConfigureAwait(false);
                results.AddRange(report.Diagnostics);
                if (report.ToolRan)
                {
                    // A file the tool analyzed with no error-severity diagnostic is proven
                    // to parse, so a compile/syntax claim against it can be refuted.
                    var erroredPaths = report.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => d.Path)
                        .ToHashSet(StringComparer.Ordinal);
                    foreach (var path in report.AnalyzedPaths.Where(p => !erroredPaths.Contains(p)))
                    {
                        cleanlyParsed.Add(path);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Verification: diagnostic provider {Provider} failed; continuing", provider.GetType().Name);
            }
        }

        return new GatheredDiagnostics(results, cleanlyParsed, workspacePath);
    }

    private static InlineComment AppendVerificationEvidence(InlineComment comment, Diagnostic evidence)
    {
        var severity = evidence.Severity == DiagnosticSeverity.Error ? "error" : "warning";
        var note = $"> ✓ **Verified** — an analyzer reports `{severity} {evidence.Code}` at line {evidence.Line}: {evidence.Message}";
        var body = string.IsNullOrWhiteSpace(comment.Body)
            ? note
            : $"{comment.Body.TrimEnd()}\n\n{note}";
        return comment with { Body = body };
    }

    private sealed record VerificationOutcome(
        IReadOnlyList<InlineComment> Comments,
        IReadOnlyList<DroppedComment> RefutedDrops);

    /// <summary>
    /// Reads a repo-relative file from the head checkout, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Rejects anything that escapes the workspace root, since the path originates in
    /// model output.
    /// </remarks>
    private static string? TryReadWorkspaceFile(string? workspacePath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(workspacePath);
            var full = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!full.StartsWith(root, StringComparison.Ordinal) || !File.Exists(full))
            {
                return null;
            }

            return File.ReadAllText(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private sealed record GatheredDiagnostics(
        IReadOnlyList<Diagnostic> Diagnostics,
        IReadOnlySet<string> CleanlyParsedPaths,
        // Where the head checkout lives, so the semantic tier can read the file a comment
        // refers to and ask the syntax tree whether the claim about it holds.
        string? WorkspacePath = null)
    {
        public static GatheredDiagnostics Empty { get; } =
            new([], new HashSet<string>(StringComparer.Ordinal));
    }

    // The summary is now synthesized from the surviving findings (see BuildFindingsSummary),
    // so a "looks good overall" summary sitting above a list of real defects is no longer
    // reachable and the phrase-matching veto that used to blank it is gone with it.
    private static ReviewResult ApplyOutputConfig(
        ReviewResult result,
        IReadOnlyList<InlineComment> comments,
        ReviewConfig config)
    {
        var summary = config.Review.Summary ? result.Summary : string.Empty;

        return summary == result.Summary && ReferenceEquals(comments, result.Comments)
            ? result
            : result with { Summary = summary, Comments = comments };
    }

    private static bool ClaimsCompileFailureContradictedByBuild(string body, GroundingContext? grounding)
    {
        // Only refute when the build actually ran and succeeded; a failed build
        // means a "won't compile" comment might well be correct. A successful build
        // proves the code compiles, so any compile/syntax-failure claim is wrong.
        return grounding?.Build is { Success: true } && CompileClaimClassifier.IsCompileFailureClaim(body);
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

    private sealed record SelfCritiqueRun(
        Task<CommentFilterResult> Task,
        CancellationTokenSource Cancellation);
}
