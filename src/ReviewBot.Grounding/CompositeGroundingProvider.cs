using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewBot.Core.Domain;
using ReviewBot.GitHub.Pulls;
using ReviewBot.Grounding.Detection;

namespace ReviewBot.Grounding;

public sealed class CompositeGroundingProvider : IGroundingProvider
{
    private static readonly GroundingContext Empty = new(null, null, null);

    private readonly IReadOnlyList<ILanguageDetector> detectors;
    private readonly Func<GroundingRequest, IRepoContentReader> readerFactory;
    private readonly ILogger<CompositeGroundingProvider> logger;

    public CompositeGroundingProvider(
        IEnumerable<ILanguageDetector> detectors,
        IGitHubClientFactory clientFactory,
        ILogger<CompositeGroundingProvider>? logger = null)
        : this(
            detectors.ToArray(),
            r => new GitHubRepoContentReader(clientFactory, r.Owner, r.Repo, r.InstallationToken),
            logger)
    {
    }

    internal CompositeGroundingProvider(
        IReadOnlyList<ILanguageDetector> detectors,
        IRepoContentReader reader,
        ILogger<CompositeGroundingProvider>? logger = null)
        : this(detectors, _ => reader, logger)
    {
    }

    private CompositeGroundingProvider(
        IReadOnlyList<ILanguageDetector> detectors,
        Func<GroundingRequest, IRepoContentReader> readerFactory,
        ILogger<CompositeGroundingProvider>? logger)
    {
        this.detectors = detectors;
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
            return new GroundingContext(language, null, null);
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
}
