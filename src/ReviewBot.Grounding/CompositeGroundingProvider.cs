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
        {
            return Empty;
        }

        try
        {
            var reader = readerFactory(request);
            var rootFiles = await reader.ListRootFilesAsync(request.HeadSha, ct).ConfigureAwait(false);
            var detector = detectors.FirstOrDefault(d => d.CanDetect(rootFiles));
            if (detector is null)
            {
                return Empty;
            }

            var language = await detector.ExtractMetadataAsync(reader, request.HeadSha, ct).ConfigureAwait(false);

            BuildResult? buildResult = null;
            if (language is not null && request.Config.Build)
            {
                buildResult = await RunBuildAsync(request, language.LanguageId, ct).ConfigureAwait(false);
            }

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

    private async Task<BuildResult?> RunBuildAsync(GroundingRequest request, string languageId, CancellationToken ct)
    {
        var runner = buildRunners.FirstOrDefault(r => r.LanguageId == languageId);
        if (runner is null || workspaceFactory is null)
            return null;

        var cloneUrl = $"https://github.com/{request.Owner}/{request.Repo}.git";
        var workspaceRequest = new WorkspaceRequest(cloneUrl, request.HeadSha, request.InstallationToken);

        IWorkspace? workspace = null;
        try
        {
            workspace = await workspaceFactory.CreateAsync(workspaceRequest, ct).ConfigureAwait(false);
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
