using System.Text;
using Microsoft.Extensions.Logging;
using Octokit;
using ReviewBot.Core.Domain;
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

    public RepoConfigFetcher(IGitHubClientFactory clientFactory, ILogger<RepoConfigFetcher> logger)
    {
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

            var yaml = await TryFetchConfigFileAsync(client, owner, repo, sha, path).ConfigureAwait(false);
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
        string path)
    {
        try
        {
            var contents = await client.Repository.Content
                .GetAllContentsByRef(owner, repo, path, sha)
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
            MergeString(fileConfig.Model?.Name, defaults.Model.Name),
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
            trigger);

        return new ReviewConfig(
            fileConfig.Enabled ?? defaults.Enabled,
            model,
            review,
            fileConfig.Ignore ?? defaults.Ignore,
            fileConfig.Focus ?? defaults.Focus,
            MergeString(fileConfig.Instructions?.Trim(), defaults.Instructions));
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
    }

    private sealed class TriggerConfigFile
    {
        public TriggerConfigFile()
        {
        }

        public bool? OnReviewRequest { get; set; }

        public bool? OnPush { get; set; }
    }
}
