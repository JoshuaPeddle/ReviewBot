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
    private const string YamlPath = ".github/review-bot.yaml";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
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

        foreach (var path in new[] { YmlPath, YamlPath })
        {
            ct.ThrowIfCancellationRequested();

            var yaml = await TryFetchConfigFileAsync(client, owner, repo, sha, path, ct).ConfigureAwait(false);
            if (yaml is null)
            {
                continue;
            }

            return ParseConfig(yaml, owner, repo, sha, path);
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
                logger.LogWarning(
                    "ReviewBot repo config {Path} for {Owner}/{Repo} at {Sha} was not a single base64-encoded file; using defaults",
                    path,
                    owner,
                    repo,
                    sha);
                return string.Empty;
            }

            return Encoding.UTF8.GetString(Convert.FromBase64String(file.EncodedContent));
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
            return MergeWithDefault(fileConfig, owner, repo, sha, path);
        }
        catch (Exception ex) when (ex is YamlException or FormatException or ArgumentException)
        {
            logger.LogWarning(
                ex,
                "Failed to parse ReviewBot repo config {Path} for {Owner}/{Repo} at {Sha}; using defaults",
                path,
                owner,
                repo,
                sha);
            return ReviewConfig.Default;
        }
    }

    private ReviewConfig MergeWithDefault(
        RepoConfigFile? fileConfig,
        string owner,
        string repo,
        string sha,
        string path)
    {
        var defaults = ReviewConfig.Default;
        if (fileConfig is null)
        {
            return defaults;
        }

        var provider = MergeProvider(fileConfig.Model?.Provider, owner, repo, sha, path);
        var model = new ModelConfig(
            provider,
            // An omitted model name stays empty so the LLM factory falls back to the provider's
            // configured model (e.g. REVIEWBOT__OpenAi__ModelName) instead of a hardcoded default.
            (fileConfig.Model?.Name ?? string.Empty).Trim(),
            MergeNullableString(fileConfig.Model?.BaseUrlEnvVar, defaults.Model.BaseUrlEnvVar));

        var trigger = new TriggerConfig(
            fileConfig.Review?.Trigger?.OnReviewRequest ?? defaults.Review.Trigger.OnReviewRequest,
            fileConfig.Review?.Trigger?.OnPush ?? defaults.Review.Trigger.OnPush);
        var review = new ReviewOutputConfig(
            fileConfig.Review?.InlineComments ?? defaults.Review.InlineComments,
            fileConfig.Review?.Summary ?? defaults.Review.Summary,
            MergePositiveInt(
                fileConfig.Review?.MaxFiles,
                defaults.Review.MaxFiles,
                "review.max_files",
                owner,
                repo,
                sha,
                path),
            MergePositiveInt(
                fileConfig.Review?.MaxPatchLines,
                defaults.Review.MaxPatchLines,
                "review.max_patch_lines",
                owner,
                repo,
                sha,
                path),
            trigger,
            ParseMinConfidence(fileConfig.Review?.MinConfidence, owner, repo, sha, path),
            fileConfig.Review?.SelfCritique ?? defaults.Review.SelfCritique,
            fileConfig.Review?.AgenticContext ?? defaults.Review.AgenticContext,
            MergePositiveInt(
                fileConfig.Review?.MaxContextRequests,
                defaults.Review.MaxContextRequests,
                "review.max_context_requests",
                owner,
                repo,
                sha,
                path),
            MergePositiveInt(
                fileConfig.Review?.MaxContextFileBytes,
                defaults.Review.MaxContextFileBytes,
                "review.max_context_file_bytes",
                owner,
                repo,
                sha,
                path),
            fileConfig.Review?.RequestChangesOnError ?? defaults.Review.RequestChangesOnError,
            fileConfig.Review?.ApproveIfClean ?? defaults.Review.ApproveIfClean,
            MergeNonNegativeInt(
                fileConfig.Review?.FullFileMaxBytes,
                defaults.Review.FullFileMaxBytes,
                "review.full_file_max_bytes",
                owner,
                repo,
                sha,
                path),
            MergeNonNegativeInt(
                fileConfig.Review?.ResponseReserveTokens,
                defaults.Review.ResponseReserveTokens,
                "review.response_reserve_tokens",
                owner,
                repo,
                sha,
                path),
            fileConfig.Review?.ChunkedReview ?? defaults.Review.ChunkedReview,
            MergePositiveInt(
                fileConfig.Review?.MaxChunks,
                defaults.Review.MaxChunks,
                "review.max_chunks",
                owner,
                repo,
                sha,
                path),
            MergeUnitInterval(
                fileConfig.Review?.ChunkHeadroom,
                defaults.Review.ChunkHeadroom,
                "review.chunk_headroom",
                owner,
                repo,
                sha,
                path));

        var grounding = MergeGrounding(fileConfig.Grounding, defaults.Grounding);
        var retrieval = MergeRetrieval(fileConfig.Retrieval, defaults.Retrieval, owner, repo, sha, path);

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

    private GroundingConfig MergeGrounding(GroundingConfigFile? file, GroundingConfig defaults)
    {
        if (file is null)
        {
            return defaults;
        }

        var localTests = file.LocalTests ?? defaults.LocalTests;

        return new GroundingConfig(
            file.Enabled ?? defaults.Enabled,
            file.Build ?? defaults.Build,
            localTests || (file.Tests ?? defaults.Tests),
            localTests,
            file.BuildTimeoutSeconds ?? defaults.BuildTimeoutSeconds,
            file.TestTimeoutSeconds ?? defaults.TestTimeoutSeconds,
            file.BuildCommand ?? defaults.BuildCommand,
            file.TestCommand ?? defaults.TestCommand);
    }

    private RetrievalConfig MergeRetrieval(
        RetrievalConfigFile? file,
        RetrievalConfig defaults,
        string owner,
        string repo,
        string sha,
        string path)
    {
        if (file is null)
        {
            return defaults;
        }

        return new RetrievalConfig(
            file.Enabled ?? defaults.Enabled,
            MergePositiveInt(
                file.MaxBytes,
                defaults.MaxBytes,
                "retrieval.max_bytes",
                owner,
                repo,
                sha,
                path),
            ParseSymbolLookupDepth(file.SymbolLookupDepth, owner, repo, sha, path),
            file.Embeddings ?? defaults.Embeddings,
            MergeString(file.IndexCacheDir, defaults.IndexCacheDir));
    }

    private string MergeProvider(string? provider, string owner, string repo, string sha, string path)
    {
        var normalized = provider?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized))
        {
            return ReviewConfig.Default.Model.Provider;
        }

        if (normalized is "anthropic" or "openai")
        {
            return normalized;
        }

        logger.LogWarning(
            "Unknown ReviewBot model provider {Provider} in {Path} for {Owner}/{Repo} at {Sha}; using default provider {DefaultProvider}",
            provider,
            path,
            owner,
            repo,
            sha,
            ReviewConfig.Default.Model.Provider);
        return ReviewConfig.Default.Model.Provider;
    }

    private int MergePositiveInt(
        int? value,
        int defaultValue,
        string fieldName,
        string owner,
        string repo,
        string sha,
        string path)
    {
        if (value is null)
        {
            return defaultValue;
        }

        if (value > 0)
        {
            return value.Value;
        }

        logger.LogWarning(
            "Invalid ReviewBot config value {FieldName}={Value} in {Path} for {Owner}/{Repo} at {Sha}; using default {DefaultValue}",
            fieldName,
            value,
            path,
            owner,
            repo,
            sha,
            defaultValue);
        return defaultValue;
    }

    private int MergeNonNegativeInt(
        int? value,
        int defaultValue,
        string fieldName,
        string owner,
        string repo,
        string sha,
        string path)
    {
        if (value is null)
        {
            return defaultValue;
        }

        if (value >= 0)
        {
            return value.Value;
        }

        logger.LogWarning(
            "Invalid ReviewBot config value {FieldName}={Value} in {Path} for {Owner}/{Repo} at {Sha}; using default {DefaultValue}",
            fieldName,
            value,
            path,
            owner,
            repo,
            sha,
            defaultValue);
        return defaultValue;
    }

    private double MergeUnitInterval(
        double? value,
        double defaultValue,
        string fieldName,
        string owner,
        string repo,
        string sha,
        string path)
    {
        if (value is null)
        {
            return defaultValue;
        }

        if (value > 0 && value <= 1)
        {
            return value.Value;
        }

        logger.LogWarning(
            "Invalid ReviewBot config value {FieldName}={Value} in {Path} for {Owner}/{Repo} at {Sha}; using default {DefaultValue}",
            fieldName,
            value,
            path,
            owner,
            repo,
            sha,
            defaultValue);
        return defaultValue;
    }

    private Confidence ParseMinConfidence(string? value, string owner, string repo, string sha, string path)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Confidence.Low;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "low" => Confidence.Low,
            "medium" => Confidence.Medium,
            "high" => Confidence.High,
            _ => LogUnknownMinConfidence(value, owner, repo, sha, path)
        };
    }

    private Confidence LogUnknownMinConfidence(string value, string owner, string repo, string sha, string path)
    {
        logger.LogWarning(
            "Unknown ReviewBot min_confidence value {Value} in {Path} for {Owner}/{Repo} at {Sha}; using default low",
            value,
            path,
            owner,
            repo,
            sha);
        return Confidence.Low;
    }

    private string ParseSymbolLookupDepth(string? value, string owner, string repo, string sha, string path)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RetrievalConfig.Default.SymbolLookupDepth;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized is RetrievalConfig.DefinitionsDepth or RetrievalConfig.CallersDepth or RetrievalConfig.BothDepth)
        {
            return normalized;
        }

        logger.LogWarning(
            "Unknown ReviewBot retrieval.symbol_lookup_depth value {Value} in {Path} for {Owner}/{Repo} at {Sha}; using default {DefaultValue}",
            value,
            path,
            owner,
            repo,
            sha,
            RetrievalConfig.Default.SymbolLookupDepth);
        return RetrievalConfig.Default.SymbolLookupDepth;
    }

    private static string MergeString(string? value, string defaultValue) =>
        string.IsNullOrWhiteSpace(value) ? defaultValue : value;

    private static string? MergeNullableString(string? value, string? defaultValue) =>
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

        public string? BaseUrlEnvVar { get; set; }
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

        public bool? ChunkedReview { get; set; }

        public int? MaxChunks { get; set; }

        public double? ChunkHeadroom { get; set; }
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

        public bool? Embeddings { get; set; }

        public string? IndexCacheDir { get; set; }
    }
}
