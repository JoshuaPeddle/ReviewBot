using System.Text;
using Microsoft.Extensions.Logging;
using Octokit;
using ReviewBot.Core.Domain;
using ReviewBot.GitHub;
using ReviewBot.GitHub.Pulls;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ReviewBot.GitHub.Config;

public sealed class RepoConfigFetcher : IRepoConfigFetcher
{
    private const string YmlPath = ".github/review-bot.yml";
    private const int MaxReviewFiles = 300;
    private const int MaxPatchLines = 20_000;
    private const int MaxContextRequests = 20;
    private const int MaxContextFileBytes = 1_048_576;
    private const int MaxResponseReserveTokens = 32_768;
    private const int MaxReviewChunks = 50;
    // Each sample is a full review, so the cap bounds a repo's ability to multiply its own
    // token spend and wall-clock by a config typo.
    private const int MaxEnsembleSamples = 9;
    private const int MaxEnsembleLineWindow = 100;
    private const int MaxBuildTimeoutSeconds = 600;
    private const int MaxTestTimeoutSeconds = 1_800;
    private const int MaxRetrievalBytes = 1_048_576;
    private const int MaxRetrievalHops = 5;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private readonly IGitHubClientFactory clientFactory;
    private readonly ILogger<RepoConfigFetcher> logger;
    private readonly TimeProvider clock;

    public RepoConfigFetcher(
        IGitHubClientFactory clientFactory,
        ILogger<RepoConfigFetcher> logger,
        TimeProvider? clock = null)
    {
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.clock = clock ?? TimeProvider.System;
    }

    public async Task<ReviewConfig> FetchAsync(
        string owner,
        string repo,
        string sha,
        string installationToken,
        CancellationToken ct)
    {
        ValidateInputs(owner, repo, sha, installationToken);
        ct.ThrowIfCancellationRequested();

        var client = clientFactory.CreateForInstallation(installationToken);

        string? yaml;
        try
        {
            yaml = await TryFetchConfigFileAsync(client, owner, repo, sha, YmlPath, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(
                ex,
                "Invalid ReviewBot repo config {Path} for {Owner}/{Repo} at {Sha}; disabling review until the config is fixed",
                YmlPath,
                owner,
                repo,
                sha);
            return ReviewConfig.Default with { Enabled = false };
        }

        if (yaml is not null)
        {
            return ParseConfig(yaml, owner, repo, sha, YmlPath);
        }

        logger.LogInformation(
            "No ReviewBot repo config found for {Owner}/{Repo} at {Sha}; using defaults",
            owner,
            repo,
            sha);
        return ReviewConfig.Default;
    }

    private async Task<string?> TryFetchConfigFileAsync(
        IGitHubClient client,
        string owner,
        string repo,
        string sha,
        string path,
        CancellationToken ct)
    {
        try
        {
            var contents = await OctokitRateLimitRetry
                .ExecuteAsync(
                    () => client.Repository.Content.GetAllContentsByRef(owner, repo, path, sha),
                    logger,
                    clock,
                    ct)
                .ConfigureAwait(false);
            var file = contents.Count == 1 ? contents[0] : null;

            if (file?.EncodedContent is null)
            {
                throw new InvalidDataException("The config was not returned as one base64-encoded file.");
            }

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(file.EncodedContent));
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("The config content was not valid base64.", ex);
            }
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    private ReviewConfig ParseConfig(string yaml, string owner, string repo, string sha, string path)
    {
        try
        {
            var fileConfig = Deserializer.Deserialize<RepoConfigFile>(yaml);
            return MergeWithDefault(fileConfig);
        }
        catch (Exception ex) when (ex is YamlException or FormatException or ArgumentException)
        {
            logger.LogWarning(
                ex,
                "Invalid ReviewBot repo config {Path} for {Owner}/{Repo} at {Sha}; disabling review until the config is fixed",
                path,
                owner,
                repo,
                sha);
            return ReviewConfig.Default with { Enabled = false };
        }
    }

    private static ReviewConfig MergeWithDefault(RepoConfigFile? fileConfig)
    {
        var defaults = ReviewConfig.Default;
        if (fileConfig is null)
        {
            return defaults;
        }

        var provider = MergeProvider(fileConfig.Model?.Provider);
        var model = new ModelConfig(
            provider,
            // An omitted model name stays empty so the LLM factory falls back to the provider's
            // configured model (e.g. REVIEWBOT__OpenAi__ModelName) instead of a hardcoded default.
            MergeModelName(fileConfig.Model?.Name));

        var trigger = new TriggerConfig(
            fileConfig.Review?.Trigger?.OnReviewRequest ?? defaults.Review.Trigger.OnReviewRequest,
            fileConfig.Review?.Trigger?.OnPush ?? defaults.Review.Trigger.OnPush);
        var review = new ReviewOutputConfig(
            fileConfig.Review?.InlineComments ?? defaults.Review.InlineComments,
            fileConfig.Review?.Summary ?? defaults.Review.Summary,
            MergeBoundedPositiveInt(
                fileConfig.Review?.MaxFiles,
                defaults.Review.MaxFiles,
                MaxReviewFiles,
                "review.max_files"),
            MergeBoundedPositiveInt(
                fileConfig.Review?.MaxPatchLines,
                defaults.Review.MaxPatchLines,
                MaxPatchLines,
                "review.max_patch_lines"),
            trigger,
            ParseMinConfidence(fileConfig.Review?.MinConfidence),
            fileConfig.Review?.SelfCritique ?? defaults.Review.SelfCritique,
            fileConfig.Review?.AgenticContext ?? defaults.Review.AgenticContext,
            MergeBoundedPositiveInt(
                fileConfig.Review?.MaxContextRequests,
                defaults.Review.MaxContextRequests,
                MaxContextRequests,
                "review.max_context_requests"),
            MergeBoundedPositiveInt(
                fileConfig.Review?.MaxContextFileBytes,
                defaults.Review.MaxContextFileBytes,
                MaxContextFileBytes,
                "review.max_context_file_bytes"),
            fileConfig.Review?.RequestChangesOnError ?? defaults.Review.RequestChangesOnError,
            fileConfig.Review?.ApproveIfClean ?? defaults.Review.ApproveIfClean,
            MergeBoundedNonNegativeInt(
                fileConfig.Review?.FullFileMaxBytes,
                defaults.Review.FullFileMaxBytes,
                MaxContextFileBytes,
                "review.full_file_max_bytes"),
            MergeBoundedNonNegativeInt(
                fileConfig.Review?.ResponseReserveTokens,
                defaults.Review.ResponseReserveTokens,
                MaxResponseReserveTokens,
                "review.response_reserve_tokens"),
            MergeBoundedPositiveInt(
                fileConfig.Review?.MaxChunks,
                defaults.Review.MaxChunks,
                MaxReviewChunks,
                "review.max_chunks"),
            MergeUnitInterval(
                fileConfig.Review?.ChunkHeadroom,
                defaults.Review.ChunkHeadroom,
                "review.chunk_headroom"))
        {
            Verification = MergeVerification(fileConfig.Review?.Verification, defaults.Review.Verification),
            Ensemble = MergeEnsemble(fileConfig.Review?.Ensemble, defaults.Review.Ensemble)
        };

        var grounding = MergeGrounding(fileConfig.Grounding, defaults.Grounding);
        var retrieval = MergeRetrieval(fileConfig.Retrieval, defaults.Retrieval);

        return new ReviewConfig(
            fileConfig.Enabled ?? defaults.Enabled,
            model,
            review,
            fileConfig.Ignore ?? defaults.Ignore,
            fileConfig.Focus ?? defaults.Focus,
            MergeString(fileConfig.Instructions?.Trim(), defaults.Instructions),
            grounding,
            retrieval);
    }

    private static VerificationConfig MergeVerification(VerificationConfigFile? file, VerificationConfig defaults)
    {
        if (file is null)
        {
            return defaults;
        }

        return new VerificationConfig(file.Enabled ?? defaults.Enabled);
    }

    private static EnsembleConfig MergeEnsemble(EnsembleConfigFile? file, EnsembleConfig defaults)
    {
        if (file is null)
        {
            return defaults;
        }

        // Each knob is validated rather than silently clamped: a repo asking for 0 samples or a
        // threshold above the sample count has made a mistake worth surfacing, and quietly
        // substituting a default is how a repo ends up running a pipeline it did not ask for.
        var samples = MergeBoundedPositiveInt(
            file.Samples, defaults.Samples, MaxEnsembleSamples, "review.ensemble.samples");
        var minAgreement = MergeBoundedPositiveInt(
            file.MinAgreement, defaults.MinAgreement, MaxEnsembleSamples, "review.ensemble.min_agreement");
        if (minAgreement > samples)
        {
            throw new ArgumentException(
                $"review.ensemble.min_agreement ({minAgreement}) cannot exceed review.ensemble.samples ({samples}).",
                nameof(file));
        }

        var lineWindow = MergeBoundedNonNegativeInt(
            file.LineWindow, defaults.LineWindow, MaxEnsembleLineWindow, "review.ensemble.line_window");

        return new EnsembleConfig(samples, minAgreement, lineWindow);
    }

    private static GroundingConfig MergeGrounding(GroundingConfigFile? file, GroundingConfig defaults)
    {
        if (file is null)
        {
            return defaults;
        }

        var localTests = file.LocalTests ?? defaults.LocalTests;
        var build = file.Build ?? defaults.Build;
        if (localTests && !build)
        {
            throw new ArgumentException(
                "grounding.local_tests requires grounding.build to be true.",
                nameof(file));
        }

        return new GroundingConfig(
            file.Enabled ?? defaults.Enabled,
            build,
            localTests || (file.Tests ?? defaults.Tests),
            localTests,
            MergeBoundedPositiveInt(
                file.BuildTimeoutSeconds,
                defaults.BuildTimeoutSeconds,
                MaxBuildTimeoutSeconds,
                "grounding.build_timeout_seconds"),
            MergeBoundedPositiveInt(
                file.TestTimeoutSeconds,
                defaults.TestTimeoutSeconds,
                MaxTestTimeoutSeconds,
                "grounding.test_timeout_seconds"),
            file.BuildCommand ?? defaults.BuildCommand,
            file.TestCommand ?? defaults.TestCommand);
    }

    private static RetrievalConfig MergeRetrieval(RetrievalConfigFile? file, RetrievalConfig defaults)
    {
        if (file is null)
        {
            return defaults;
        }

        return new RetrievalConfig(
            file.Enabled ?? defaults.Enabled,
            MergeBoundedPositiveInt(
                file.MaxBytes,
                defaults.MaxBytes,
                MaxRetrievalBytes,
                "retrieval.max_bytes"),
            ParseSymbolLookupDepth(file.SymbolLookupDepth),
            // Cache placement is a host concern. Never allow repository YAML to
            // choose a writable path in the ReviewBot container.
            defaults.IndexCacheDir,
            MergeBoundedPositiveInt(
                file.MaxHops,
                defaults.MaxHops,
                MaxRetrievalHops,
                "retrieval.max_hops"));
    }

    private static string MergeProvider(string? provider)
    {
        if (provider is null)
        {
            return ReviewConfig.Default.Model.Provider;
        }

        var normalized = provider.Trim().ToLowerInvariant();
        if (normalized is "anthropic" or "openai")
        {
            return normalized;
        }

        throw new ArgumentException(
            $"model.provider must be 'anthropic' or 'openai', but was '{provider}'.",
            nameof(provider));
    }

    private static string MergeModelName(string? modelName)
    {
        if (modelName is null)
        {
            return string.Empty;
        }

        var normalized = modelName.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException(
                "model.name must be omitted to use the host model, or contain a non-empty model identifier.",
                nameof(modelName));
        }

        return normalized;
    }

    private static int MergeBoundedPositiveInt(
        int? value,
        int defaultValue,
        int maximumValue,
        string fieldName)
    {
        if (value is null)
        {
            return defaultValue;
        }

        if (value > 0 && value <= maximumValue)
        {
            return value.Value;
        }

        throw new ArgumentOutOfRangeException(
            fieldName,
            value,
            $"{fieldName} must be between 1 and {maximumValue}.");
    }

    private static int MergeBoundedNonNegativeInt(
        int? value,
        int defaultValue,
        int maximumValue,
        string fieldName)
    {
        if (value is null)
        {
            return defaultValue;
        }

        if (value >= 0 && value <= maximumValue)
        {
            return value.Value;
        }

        throw new ArgumentOutOfRangeException(
            fieldName,
            value,
            $"{fieldName} must be between 0 and {maximumValue}.");
    }

    private static double MergeUnitInterval(
        double? value,
        double defaultValue,
        string fieldName)
    {
        if (value is null)
        {
            return defaultValue;
        }

        if (value > 0 && value <= 1)
        {
            return value.Value;
        }

        throw new ArgumentOutOfRangeException(fieldName, value, $"{fieldName} must be greater than 0 and at most 1.");
    }

    private static Confidence ParseMinConfidence(string? value)
    {
        if (value is null)
        {
            return ReviewConfig.Default.Review.MinConfidence;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "low" => Confidence.Low,
            "medium" => Confidence.Medium,
            "high" => Confidence.High,
            _ => throw new ArgumentException(
                $"review.min_confidence must be 'low', 'medium', or 'high', but was '{value}'.",
                nameof(value))
        };
    }

    private static string ParseSymbolLookupDepth(string? value)
    {
        if (value is null)
        {
            return RetrievalConfig.Default.SymbolLookupDepth;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized is RetrievalConfig.DefinitionsDepth or RetrievalConfig.CallersDepth or RetrievalConfig.BothDepth)
        {
            return normalized;
        }

        throw new ArgumentException(
            $"retrieval.symbol_lookup_depth must be 'definitions', 'callers', or 'both', but was '{value}'.",
            nameof(value));
    }

    private static string MergeString(string? value, string defaultValue) =>
        string.IsNullOrWhiteSpace(value) ? defaultValue : value;

    private static void ValidateInputs(string owner, string repo, string sha, string installationToken)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new ArgumentException("Repository owner must be provided.", nameof(owner));
        }

        if (string.IsNullOrWhiteSpace(repo))
        {
            throw new ArgumentException("Repository name must be provided.", nameof(repo));
        }

        if (string.IsNullOrWhiteSpace(sha))
        {
            throw new ArgumentException("Config ref SHA must be provided.", nameof(sha));
        }

        if (string.IsNullOrWhiteSpace(installationToken))
        {
            throw new ArgumentException("GitHub installation token must be provided.", nameof(installationToken));
        }
    }

    private sealed class RepoConfigFile
    {
        public RepoConfigFile()
        {
        }

        public bool? Enabled { get; set; }

        public ModelConfigFile? Model { get; set; }

        public ReviewConfigFile? Review { get; set; }

        public List<string>? Ignore { get; set; }

        public List<string>? Focus { get; set; }

        public string? Instructions { get; set; }

        public GroundingConfigFile? Grounding { get; set; }

        public RetrievalConfigFile? Retrieval { get; set; }
    }

    private sealed class ModelConfigFile
    {
        public ModelConfigFile()
        {
        }

        public string? Provider { get; set; }

        public string? Name { get; set; }
    }

    private sealed class ReviewConfigFile
    {
        public ReviewConfigFile()
        {
        }

        public bool? InlineComments { get; set; }

        public bool? Summary { get; set; }

        public int? MaxFiles { get; set; }

        public int? MaxPatchLines { get; set; }

        public TriggerConfigFile? Trigger { get; set; }

        public string? MinConfidence { get; set; }

        public bool? SelfCritique { get; set; }

        public bool? AgenticContext { get; set; }

        public int? MaxContextRequests { get; set; }

        public int? MaxContextFileBytes { get; set; }

        public bool? RequestChangesOnError { get; set; }

        public bool? ApproveIfClean { get; set; }

        public int? FullFileMaxBytes { get; set; }

        public int? ResponseReserveTokens { get; set; }

        public int? MaxChunks { get; set; }

        public double? ChunkHeadroom { get; set; }

        public VerificationConfigFile? Verification { get; set; }

        public EnsembleConfigFile? Ensemble { get; set; }
    }

    private sealed class EnsembleConfigFile
    {
        public EnsembleConfigFile()
        {
        }

        public int? Samples { get; set; }

        public int? MinAgreement { get; set; }

        public int? LineWindow { get; set; }
    }

    private sealed class VerificationConfigFile
    {
        public VerificationConfigFile()
        {
        }

        public bool? Enabled { get; set; }
    }

    private sealed class TriggerConfigFile
    {
        public TriggerConfigFile()
        {
        }

        public bool? OnReviewRequest { get; set; }

        public bool? OnPush { get; set; }
    }

    private sealed class GroundingConfigFile
    {
        public GroundingConfigFile()
        {
        }

        public bool? Enabled { get; set; }

        public bool? Build { get; set; }

        public bool? Tests { get; set; }

        public bool? LocalTests { get; set; }

        public int? BuildTimeoutSeconds { get; set; }

        public int? TestTimeoutSeconds { get; set; }

        public string? BuildCommand { get; set; }

        public string? TestCommand { get; set; }
    }

    private sealed class RetrievalConfigFile
    {
        public RetrievalConfigFile()
        {
        }

        public bool? Enabled { get; set; }

        public int? MaxBytes { get; set; }

        public string? SymbolLookupDepth { get; set; }

        public int? MaxHops { get; set; }
    }
}
