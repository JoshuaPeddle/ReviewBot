using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewBot.Core.Domain;
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
    private readonly IWorkspaceFactory? workspaceFactory;
    private readonly Func<GroundingRequest, IRepoContentReader> readerFactory;
    private readonly ILogger<CompositeGroundingProvider> logger;

    public CompositeGroundingProvider(
        IEnumerable<ILanguageDetector> detectors,
        IEnumerable<IBuildRunner> buildRunners,
        IWorkspaceFactory workspaceFactory,
        IGitHubClientFactory clientFactory,
        ILogger<CompositeGroundingProvider>? logger = null)
        : this(
            detectors.ToArray(),
            buildRunners.ToArray(),
            workspaceFactory,
            r => new GitHubRepoContentReader(clientFactory, r.Owner, r.Repo, r.InstallationToken),
            logger)
    {
    }

    internal CompositeGroundingProvider(
        IReadOnlyList<ILanguageDetector> detectors,
        IRepoContentReader reader,
        ILogger<CompositeGroundingProvider>? logger = null)
        : this(detectors, [], null, _ => reader, logger)
    {
    }

    internal CompositeGroundingProvider(
        IReadOnlyList<ILanguageDetector> detectors,
        IReadOnlyList<IBuildRunner> buildRunners,
        IWorkspaceFactory workspaceFactory,
        IRepoContentReader reader,
        ILogger<CompositeGroundingProvider>? logger = null)
        : this(detectors, buildRunners, workspaceFactory, _ => reader, logger)
    {
    }

    private CompositeGroundingProvider(
        IReadOnlyList<ILanguageDetector> detectors,
        IReadOnlyList<IBuildRunner> buildRunners,
        IWorkspaceFactory? workspaceFactory,
        Func<GroundingRequest, IRepoContentReader> readerFactory,
        ILogger<CompositeGroundingProvider>? logger)
    {
        this.detectors = detectors;
        this.buildRunners = buildRunners;
        this.workspaceFactory = workspaceFactory;
        this.readerFactory = readerFactory;
        this.logger = logger ?? NullLogger<CompositeGroundingProvider>.Instance;
    }

    public async Task<GroundingContext> GetContextAsync(GroundingRequest request, CancellationToken ct)
    {
        if (!request.Config.Enabled)
            return Empty;

        try
        {
            var reader = readerFactory(request);
            var rootFiles = await reader.ListRootFilesAsync(request.HeadSha, ct).ConfigureAwait(false);
            var detector = detectors.FirstOrDefault(d => d.CanDetect(rootFiles));
            if (detector is null)
                return Empty;

            // Start the workspace clone concurrently with Tier 1 metadata extraction.
            // The clone URL is known as soon as the detector matches; we don't need metadata first.
            Task<IWorkspace?>? cloneTask = null;
            if (request.Config.Build && workspaceFactory is not null)
                cloneTask = StartCloneAsync(request, ct);

            LanguageMetadata? language = null;
            try
            {
                language = await detector.ExtractMetadataAsync(reader, request.HeadSha, ct).ConfigureAwait(false);
            }
            catch
            {
                // Metadata extraction failed — cancel and dispose any in-flight clone to avoid leaks.
                if (cloneTask is not null)
                    await CancelAndDisposeCloneAsync(cloneTask).ConfigureAwait(false);
                throw;
            }

            BuildResult? buildResult = null;
            if (language is not null && cloneTask is not null)
                buildResult = await RunBuildOnCloneAsync(cloneTask, language.LanguageId, request, ct).ConfigureAwait(false);

            return new GroundingContext(language, buildResult, null);
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

    private Task<IWorkspace?> StartCloneAsync(GroundingRequest request, CancellationToken ct)
    {
        var cloneUrl = $"https://github.com/{request.Owner}/{request.Repo}.git";
        var workspaceRequest = new WorkspaceRequest(cloneUrl, request.HeadSha, request.InstallationToken);
        return CloneWorkspaceAsync(workspaceRequest, request, ct);
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

    private async Task<BuildResult?> RunBuildOnCloneAsync(
        Task<IWorkspace?> cloneTask, string languageId, GroundingRequest request, CancellationToken ct)
    {
        var runner = buildRunners.FirstOrDefault(r => r.LanguageId == languageId);

        IWorkspace? workspace = null;
        try
        {
            workspace = await cloneTask.ConfigureAwait(false);
            if (workspace is null || runner is null)
                return null;

            return await runner.RunAsync(workspace.LocalPath, request.Config, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Build grounding failed for {Owner}/{Repo}; proceeding without build result",
                request.Owner, request.Repo);
            return new BuildResult(false, 0, 0, ex.Message);
        }
        finally
        {
            if (workspace is not null)
                await workspace.DisposeAsync().ConfigureAwait(false);
        }
    }
}
