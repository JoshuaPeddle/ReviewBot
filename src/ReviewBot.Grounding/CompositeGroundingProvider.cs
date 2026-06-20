using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Otel;
using ReviewBot.GitHub.Checks;
using ReviewBot.GitHub.Pulls;
using ReviewBot.Grounding.Build;
using ReviewBot.Grounding.Detection;
using ReviewBot.Grounding.Workspace;

namespace ReviewBot.Grounding;

public sealed class CompositeGroundingProvider : IGroundingProvider
{
    private static readonly GroundingContext Empty = new(null, null, null);

    private readonly IReadOnlyList<ILanguageDetector> detectors;
    private readonly IReadOnlyList<IBuildRunner> buildRunners;
    private readonly IReadOnlyList<ITestRunner> testRunners;
    private readonly ICheckRunFetcher? checkRunFetcher;
    private readonly IWorkspaceFactory? workspaceFactory;
    private readonly Func<GroundingRequest, IRepoContentReader> readerFactory;
    private readonly ILogger<CompositeGroundingProvider> logger;

    public CompositeGroundingProvider(
        IEnumerable<ILanguageDetector> detectors,
        IEnumerable<IBuildRunner> buildRunners,
        IEnumerable<ITestRunner> testRunners,
        IWorkspaceFactory workspaceFactory,
        IGitHubClientFactory clientFactory,
        ICheckRunFetcher? checkRunFetcher = null,
        ILogger<CompositeGroundingProvider>? logger = null)
        : this(
            detectors.ToArray(),
            buildRunners.ToArray(),
            testRunners.ToArray(),
            workspaceFactory,
            checkRunFetcher,
            r => new GitHubRepoContentReader(clientFactory, r.Owner, r.Repo, r.InstallationToken),
            logger)
    {
    }

    internal CompositeGroundingProvider(
        IReadOnlyList<ILanguageDetector> detectors,
        IRepoContentReader reader,
        ILogger<CompositeGroundingProvider>? logger = null)
        : this(detectors, [], [], null, null, _ => reader, logger)
    {
    }

    internal CompositeGroundingProvider(
        IReadOnlyList<ILanguageDetector> detectors,
        IReadOnlyList<IBuildRunner> buildRunners,
        IWorkspaceFactory workspaceFactory,
        IRepoContentReader reader,
        ILogger<CompositeGroundingProvider>? logger = null)
        : this(detectors, buildRunners, [], workspaceFactory, null, _ => reader, logger)
    {
    }

    internal CompositeGroundingProvider(
        IReadOnlyList<ILanguageDetector> detectors,
        IReadOnlyList<IBuildRunner> buildRunners,
        IReadOnlyList<ITestRunner> testRunners,
        IWorkspaceFactory? workspaceFactory,
        ICheckRunFetcher? checkRunFetcher,
        IRepoContentReader reader,
        ILogger<CompositeGroundingProvider>? logger = null)
        : this(detectors, buildRunners, testRunners, workspaceFactory, checkRunFetcher, _ => reader, logger)
    {
    }

    private CompositeGroundingProvider(
        IReadOnlyList<ILanguageDetector> detectors,
        IReadOnlyList<IBuildRunner> buildRunners,
        IReadOnlyList<ITestRunner> testRunners,
        IWorkspaceFactory? workspaceFactory,
        ICheckRunFetcher? checkRunFetcher,
        Func<GroundingRequest, IRepoContentReader> readerFactory,
        ILogger<CompositeGroundingProvider>? logger)
    {
        this.detectors = detectors;
        this.buildRunners = buildRunners;
        this.testRunners = testRunners;
        this.workspaceFactory = workspaceFactory;
        this.checkRunFetcher = checkRunFetcher;
        this.readerFactory = readerFactory;
        this.logger = logger ?? NullLogger<CompositeGroundingProvider>.Instance;
    }

    public async Task<GroundingContext> GetContextAsync(
        GroundingRequest request,
        CancellationToken ct,
        ISharedWorkspace? sharedWorkspace = null)
    {
        if (!request.Config.Enabled)
            return Empty;

        // When a job-scoped workspace is supplied, grounding clones through it and
        // leaves disposal to the scope so retrieval and verification can reuse the
        // same checkout. Without one, grounding owns and disposes its own clone.
        var ownsWorkspace = sharedWorkspace is null;

        try
        {
            var checkResult = await TryGetHeadCheckSummaryAsync(request, ct).ConfigureAwait(false);
            var reader = readerFactory(request);
            LanguageMetadata? language = null;
            Task<IWorkspace?>? cloneTask = null;

            using (var languageActivity = ReviewBotActivitySource.Instance.StartActivity("reviewbot.grounding.tier1_language"))
            {
                var rootFiles = await reader.ListRootFilesAsync(request.HeadSha, ct).ConfigureAwait(false);
                languageActivity?.SetTag("grounding.root_files", rootFiles.Count);

                var detector = detectors.FirstOrDefault(d => d.CanDetect(rootFiles));
                if (detector is null)
                {
                    languageActivity?.SetTag("grounding.language_detected", false);
                    return checkResult is null ? Empty : new GroundingContext(null, null, checkResult);
                }

                languageActivity?.SetTag("grounding.detector", detector.LanguageId);

                // Start the workspace clone concurrently with Tier 1 metadata extraction.
                // The clone URL is known as soon as the detector matches; we don't need metadata first.
                if (request.Config.Build && (sharedWorkspace is not null || workspaceFactory is not null))
                    cloneTask = StartCloneAsync(request, sharedWorkspace, ct);

                try
                {
                    language = await detector.ExtractMetadataAsync(reader, request.HeadSha, ct).ConfigureAwait(false);
                    languageActivity?.SetTag("grounding.language_detected", language is not null);
                    if (language is not null)
                        languageActivity?.SetTag("grounding.language_id", language.LanguageId);
                }
                catch
                {
                    // Metadata extraction failed — cancel and dispose any in-flight clone to avoid leaks.
                    // A shared clone is owned by the scope; leave it for other consumers / scope disposal.
                    if (cloneTask is not null && ownsWorkspace)
                        await CancelAndDisposeCloneAsync(cloneTask).ConfigureAwait(false);
                    throw;
                }
            }

            BuildResult? buildResult = null;
            TestResult? testResult = checkResult;
            if (language is not null && cloneTask is not null)
            {
                var localResult = await RunBuildAndMaybeTestsOnCloneAsync(
                        cloneTask,
                        language.LanguageId,
                        request,
                        checkResult,
                        ownsWorkspace,
                        ct)
                    .ConfigureAwait(false);
                buildResult = localResult.Build;
                testResult = localResult.Tests;
            }

            return new GroundingContext(language, buildResult, testResult);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Grounding failed for {Owner}/{Repo} at {Sha}; proceeding without grounding",
                request.Owner,
                request.Repo,
                request.HeadSha);
            return Empty;
        }
    }

    private async Task<TestResult?> TryGetHeadCheckSummaryAsync(GroundingRequest request, CancellationToken ct)
    {
        if (!request.Config.Tests || checkRunFetcher is null)
            return null;

        using var activity = ReviewBotActivitySource.Instance.StartActivity("reviewbot.grounding.tier3_tests");
        activity?.SetTag("grounding.test_source", "github_checks");

        try
        {
            var result = await checkRunFetcher
                .GetHeadCheckSummaryAsync(
                    request.Owner,
                    request.Repo,
                    request.HeadSha,
                    request.InstallationToken,
                    ct)
                .ConfigureAwait(false);
            activity?.SetTag("grounding.tests_found", result is not null);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "GitHub Checks grounding failed for {Owner}/{Repo} at {Sha}; proceeding without checks",
                request.Owner,
                request.Repo,
                request.HeadSha);
            return null;
        }
    }

    private Task<IWorkspace?> StartCloneAsync(
        GroundingRequest request,
        ISharedWorkspace? sharedWorkspace,
        CancellationToken ct)
    {
        // Prefer the PR head's clone URL (a fork remote for cross-repo PRs); the
        // base-repo URL only holds the head SHA for same-repo branches.
        var cloneUrl = string.IsNullOrWhiteSpace(request.HeadCloneUrl)
            ? $"https://github.com/{request.Owner}/{request.Repo}.git"
            : request.HeadCloneUrl;
        var workspaceRequest = new WorkspaceRequest(cloneUrl, request.HeadSha, request.InstallationToken);
        return sharedWorkspace is not null
            ? CloneSharedWorkspaceAsync(sharedWorkspace, workspaceRequest, request, ct)
            : CloneWorkspaceAsync(workspaceRequest, request, ct);
    }

    private async Task<IWorkspace?> CloneSharedWorkspaceAsync(
        ISharedWorkspace sharedWorkspace,
        WorkspaceRequest workspaceRequest,
        GroundingRequest request,
        CancellationToken ct)
    {
        try
        {
            return await sharedWorkspace.GetOrCreateAsync(workspaceRequest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Shared workspace clone failed for {Owner}/{Repo}; build grounding skipped",
                request.Owner, request.Repo);
            return null;
        }
    }

    private async Task<IWorkspace?> CloneWorkspaceAsync(
        WorkspaceRequest workspaceRequest, GroundingRequest request, CancellationToken ct)
    {
        try
        {
            return await workspaceFactory!.CreateAsync(workspaceRequest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Workspace clone failed for {Owner}/{Repo}; build grounding skipped",
                request.Owner, request.Repo);
            return null;
        }
    }

    private static async Task CancelAndDisposeCloneAsync(Task<IWorkspace?> cloneTask)
    {
        // OperationCanceledException propagates — the outer catch handles it.
        var workspace = await cloneTask.ConfigureAwait(false);
        if (workspace is not null)
            await workspace.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<(BuildResult? Build, TestResult? Tests)> RunBuildAndMaybeTestsOnCloneAsync(
        Task<IWorkspace?> cloneTask,
        string languageId,
        GroundingRequest request,
        TestResult? checkResult,
        bool ownsWorkspace,
        CancellationToken ct)
    {
        using var buildActivity = ReviewBotActivitySource.Instance.StartActivity("reviewbot.grounding.tier2_build");
        buildActivity?.SetTag("grounding.language_id", languageId);

        var buildRunner = buildRunners.FirstOrDefault(r => r.LanguageId == languageId);

        IWorkspace? workspace = null;
        try
        {
            workspace = await cloneTask.ConfigureAwait(false);
            if (workspace is null || buildRunner is null)
            {
                buildActivity?.SetTag("grounding.build_ran", false);
                return (null, checkResult);
            }

            var buildResult = await buildRunner.RunAsync(workspace.LocalPath, request.Config, ct).ConfigureAwait(false);
            buildActivity?.SetTag("grounding.build_ran", true);
            buildActivity?.SetTag("grounding.build_success", buildResult.Success);

            var testResult = checkResult;
            if (request.Config.LocalTests && buildResult.Success)
            {
                var localTests = await RunLocalTestsAsync(workspace.LocalPath, languageId, request.Config, checkResult, ct)
                    .ConfigureAwait(false);
                testResult = localTests ?? checkResult;
            }

            return (buildResult, testResult);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            buildActivity?.SetTag("grounding.build_ran", true);
            buildActivity?.SetTag("grounding.build_success", false);
            logger.LogWarning(ex, "Build grounding failed for {Owner}/{Repo}; proceeding without build result",
                request.Owner, request.Repo);
            return (new BuildResult(false, 0, 0, ex.Message), checkResult);
        }
        finally
        {
            // A shared clone is owned by the job scope; only dispose a clone we own.
            if (workspace is not null && ownsWorkspace)
                await workspace.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<TestResult?> RunLocalTestsAsync(
        string workspacePath,
        string languageId,
        GroundingConfig config,
        TestResult? checkResult,
        CancellationToken ct)
    {
        var runner = testRunners.FirstOrDefault(r => r.LanguageId == languageId);
        if (runner is null)
            return null;

        using var activity = ReviewBotActivitySource.Instance.StartActivity("reviewbot.grounding.tier3_tests");
        activity?.SetTag("grounding.test_source", "local");
        activity?.SetTag("grounding.language_id", languageId);

        try
        {
            var testResult = await runner.RunAsync(workspacePath, config, ct).ConfigureAwait(false);
            activity?.SetTag("grounding.tests_found", true);
            activity?.SetTag("grounding.tests_failed", testResult.Failed);
            return AppendCheckOutput(testResult, checkResult);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetTag("grounding.tests_found", true);
            activity?.SetTag("grounding.tests_failed", 0);
            logger.LogWarning(ex, "Test grounding failed; proceeding with failed local test result");
            return AppendCheckOutput(new TestResult(0, 0, 0, ex.Message), checkResult);
        }
    }

    private static TestResult AppendCheckOutput(TestResult localResult, TestResult? checkResult)
    {
        if (checkResult is null || string.IsNullOrWhiteSpace(checkResult.Output))
            return localResult;

        var output = string.IsNullOrWhiteSpace(localResult.Output)
            ? $"GitHub Checks:\n{checkResult.Output}"
            : $"{localResult.Output}\n\nGitHub Checks:\n{checkResult.Output}";
        return localResult with { Output = output };
    }
}
