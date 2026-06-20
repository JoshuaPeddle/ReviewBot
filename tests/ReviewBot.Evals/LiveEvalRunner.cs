using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewBot.Core.Context;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;
using ReviewBot.Llm.OpenAi;
using ReviewBot.Retrieval;
using ReviewBot.Retrieval.Indexing;
using ReviewBot.Retrieval.Symbols;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ReviewBot.Evals;

public sealed class LiveEvalRunner
{
    private const string Owner = "eval";
    private const string Repo = "reviewbot";
    private const string HeadSha = "fixture-head";
    private const string BaseSha = "fixture-base";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly EvalFixtureLoader loader;

    public LiveEvalRunner(EvalFixtureLoader? loader = null)
    {
        this.loader = loader ?? new EvalFixtureLoader();
    }

    public async Task<IReadOnlyList<LiveEvalFixtureResult>> RunAsync(
        LiveEvalOptions options,
        TextWriter output,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        Directory.CreateDirectory(options.ResultsDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ManifestPath))!);
        if (options.RetrievalEnabled)
        {
            Directory.CreateDirectory(options.IndexCacheDir);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var config = LoadConfig(options);
        var llm = new OpenAiReviewLlm(
            new OpenAiLlmOptions
            {
                ApiKey = options.ApiKey,
                BaseUrl = options.BaseUrl,
                ModelName = options.Model,
                ResponseFormat = "text",
                Temperature = options.Temperature,
                MaxTokens = options.MaxTokens,
                TimeoutSeconds = options.RequestTimeoutSeconds
            },
            NullLogger<OpenAiReviewLlm>.Instance);
        var fixtures = Directory
            .EnumerateDirectories(options.FixturesDirectory)
            .Where(directory => File.Exists(Path.Combine(directory, "fixture.yaml")))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var results = new List<LiveEvalFixtureResult>();
        var manifestFixtures = new List<LiveEvalFixtureManifest>();

        foreach (var fixtureDirectory in fixtures)
        {
            ct.ThrowIfCancellationRequested();

            var fixture = loader.Load(fixtureDirectory);
            var requestContext = await BuildRequestAsync(fixture, config, options, ct).ConfigureAwait(false);
            await output.WriteLineAsync(
                $"Running {Path.GetFileName(fixtureDirectory)} (retrieval={options.RetrievalEnabled.ToString().ToLowerInvariant()}, snippets={requestContext.Snippets.Count})")
                .ConfigureAwait(false);

            var fixtureStartedAt = DateTimeOffset.UtcNow;
            ReviewResult result;
            string status;
            using (var fixtureCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                fixtureCts.CancelAfter(TimeSpan.FromSeconds(options.PerFixtureTimeoutSeconds));
                try
                {
                    result = await llm.ReviewAsync(requestContext.Request, fixtureCts.Token).ConfigureAwait(false);
                    status = "succeeded";
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    var elapsedSeconds = (DateTimeOffset.UtcNow - fixtureStartedAt).TotalSeconds;
                    var isTimeout = fixtureCts.IsCancellationRequested ||
                        ex is OperationCanceledException ||
                        ContainsCancellationOrTimeout(ex);
                    var reason = isTimeout
                        ? (fixtureCts.IsCancellationRequested
                            ? $"hit per-fixture timeout ({options.PerFixtureTimeoutSeconds}s)"
                            : $"LLM transport timed out: {ex.GetBaseException().Message}")
                        : $"LLM error: {ex.GetBaseException().Message}";
                    await output.WriteLineAsync(
                        $"FAIL {Path.GetFileName(fixtureDirectory)} after {elapsedSeconds:F0}s ({reason}); writing empty result and continuing.")
                        .ConfigureAwait(false);
                    result = new ReviewResult(
                        Summary: $"Eval fixture aborted: {reason}.",
                        Comments: Array.Empty<InlineComment>(),
                        ContextRequests: Array.Empty<ContextRequest>());
                    status = isTimeout ? "timed_out" : "errored";
                }
            }

            var outputPath = Path.Combine(options.ResultsDirectory, $"{Path.GetFileName(fixtureDirectory)}.json");
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(result, JsonOptions), ct).ConfigureAwait(false);
            results.Add(new LiveEvalFixtureResult(
                Path.GetFileName(fixtureDirectory),
                outputPath,
                result.Comments.Count,
                requestContext.Snippets.Count,
                result.TokenUsage));
            manifestFixtures.Add(new LiveEvalFixtureManifest(
                FixtureKey: Path.GetFileName(fixtureDirectory),
                FixtureName: fixture.Metadata.Name,
                Category: fixture.Metadata.Category,
                ResultPath: outputPath,
                Status: status,
                ElapsedSeconds: (DateTimeOffset.UtcNow - fixtureStartedAt).TotalSeconds,
                CommentCount: result.Comments.Count,
                RetrievalSnippetCount: requestContext.Snippets.Count,
                RetrievalSymbolsQueried: requestContext.SymbolsQueried,
                RetrievalSnippets: requestContext.Snippets,
                TokenUsage: result.TokenUsage));
        }

        var manifest = new LiveEvalManifest(
            StartedAtUtc: startedAt,
            FinishedAtUtc: DateTimeOffset.UtcNow,
            FixturesDirectory: options.FixturesDirectory,
            ResultsDirectory: options.ResultsDirectory,
            BaseUrl: options.BaseUrl.ToString(),
            Model: options.Model,
            RetrievalEnabled: options.RetrievalEnabled,
            ConfigPath: options.ConfigPath,
            ContextTokens: options.ContextTokens,
            IndexCacheDir: options.IndexCacheDir,
            Fixtures: manifestFixtures);
        await File.WriteAllTextAsync(options.ManifestPath, JsonSerializer.Serialize(manifest, JsonOptions), ct)
            .ConfigureAwait(false);

        return results;
    }

    private static bool ContainsCancellationOrTimeout(Exception exception)
    {
        // The OpenAI SDK's ClientRetryPolicy wraps retry failures in
        // AggregateException, so a per-fixture timeout that fires while the
        // SDK is mid-retry surfaces as a non-cancellation outer exception.
        // Walk inner / aggregate exceptions to detect the cancellation root.
        if (exception is OperationCanceledException or TimeoutException)
        {
            return true;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (ContainsCancellationOrTimeout(inner))
                {
                    return true;
                }
            }
        }

        return exception.InnerException is not null && ContainsCancellationOrTimeout(exception.InnerException);
    }

    private static async Task<LiveEvalRequestContext> BuildRequestAsync(
        EvalFixture fixture,
        ReviewConfig config,
        LiveEvalOptions options,
        CancellationToken ct)
    {
        var files = EvalDiffParser.ParseFiles(fixture.DiffPatch);
        var request = new ReviewRequest(
            PrTitle: fixture.Metadata.Name,
            PrBody: fixture.Metadata.Description,
            BaseSha: BaseSha,
            HeadSha: HeadSha,
            Files: files,
            Config: config);

        if (!options.RetrievalEnabled)
        {
            return new LiveEvalRequestContext(request, [], 0);
        }

        var repoState = Path.Combine(fixture.DirectoryPath, "repo-state");
        if (!Directory.Exists(repoState))
        {
            return new LiveEvalRequestContext(request, [], 0);
        }

        var estimator = new HeuristicTokenEstimator();
        var factory = new SqliteRepoIndexFactory([new CSharpRepoSymbolParser()], TimeProvider.System);
        var index = factory.Create(options.IndexCacheDir);
        await index.IndexAsync(new RepoIndexRequest(Owner, Repo, HeadSha, repoState, config.Ignore), ct)
            .ConfigureAwait(false);

        var provider = new SqliteRetrievalProvider(factory, new CSharpDiffSymbolExtractor(), estimator);
        var systemPromptTokens = estimator.EstimateTokens(ReviewBot.Core.Prompting.PromptBuilder.Build(request).SystemPrompt);
        var budget = PromptBudget.Create(
            options.ContextTokens,
            systemPromptTokens,
            groundingTokens: 0,
            config.Review.ResponseReserveTokens);
        var retrieval = await provider.GetContextAsync(Owner, Repo, request, budget, ct).ConfigureAwait(false);
        var snippets = retrieval.Snippets
            .Select(snippet => new LiveEvalRetrievalSnippet(
                snippet.Path,
                snippet.StartLine,
                snippet.EndLine,
                estimator.EstimateTokens(snippet.Content),
                HashContent(snippet.Content)))
            .ToArray();
        return new LiveEvalRequestContext(
            request with { RepositoryContext = retrieval.Snippets },
            snippets,
            retrieval.SymbolsQueried);
    }

    private static string HashContent(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static ReviewConfig LoadConfig(LiveEvalOptions options)
    {
        var config = ReviewConfig.Default with
        {
            Model = new ModelConfig("openai", options.Model, null),
            Retrieval = ReviewConfig.Default.Retrieval with
            {
                Enabled = options.RetrievalEnabled,
                IndexCacheDir = options.IndexCacheDir
            }
        };

        if (options.ConfigPath is null || !File.Exists(options.ConfigPath))
        {
            return config;
        }

        var file = YamlDeserializer.Deserialize<ReferenceConfigFile>(File.ReadAllText(options.ConfigPath));
        if (file is null)
        {
            return config;
        }

        return config with
        {
            Model = config.Model with
            {
                Provider = string.IsNullOrWhiteSpace(file.Model?.Provider) ? config.Model.Provider : file.Model.Provider.Trim(),
                Name = string.IsNullOrWhiteSpace(file.Model?.Name) ? config.Model.Name : file.Model.Name.Trim()
            },
            Review = config.Review with
            {
                InlineComments = file.Review?.InlineComments ?? config.Review.InlineComments,
                Summary = file.Review?.Summary ?? config.Review.Summary,
                MaxFiles = PositiveOrDefault(file.Review?.MaxFiles, config.Review.MaxFiles),
                MaxPatchLines = PositiveOrDefault(file.Review?.MaxPatchLines, config.Review.MaxPatchLines),
                ResponseReserveTokens = NonNegativeOrDefault(file.Review?.ResponseReserveTokens, config.Review.ResponseReserveTokens),
                ChunkedReview = file.Review?.ChunkedReview ?? config.Review.ChunkedReview,
                MaxChunks = PositiveOrDefault(file.Review?.MaxChunks, config.Review.MaxChunks),
                ChunkHeadroom = UnitIntervalOrDefault(file.Review?.ChunkHeadroom, config.Review.ChunkHeadroom)
            },
            Ignore = file.Ignore ?? config.Ignore,
            Focus = file.Focus ?? config.Focus,
            Instructions = string.IsNullOrWhiteSpace(file.Instructions) ? config.Instructions : file.Instructions.Trim(),
            Grounding = config.Grounding with { Enabled = false, Build = false, Tests = false, LocalTests = false },
            Retrieval = config.Retrieval with
            {
                MaxBytes = PositiveOrDefault(file.Retrieval?.MaxBytes, config.Retrieval.MaxBytes),
                SymbolLookupDepth = string.IsNullOrWhiteSpace(file.Retrieval?.SymbolLookupDepth)
                    ? config.Retrieval.SymbolLookupDepth
                    : file.Retrieval.SymbolLookupDepth.Trim(),
                Embeddings = file.Retrieval?.Embeddings ?? config.Retrieval.Embeddings,
                IndexCacheDir = options.IndexCacheDir
            }
        };
    }

    private static int PositiveOrDefault(int? value, int defaultValue) =>
        value is > 0 ? value.Value : defaultValue;

    private static int NonNegativeOrDefault(int? value, int defaultValue) =>
        value is >= 0 ? value.Value : defaultValue;

    private static double UnitIntervalOrDefault(double? value, double defaultValue) =>
        value is > 0 and <= 1 ? value.Value : defaultValue;

    private sealed class ReferenceConfigFile
    {
        public ModelFile? Model { get; set; }

        public ReviewFile? Review { get; set; }

        public List<string>? Ignore { get; set; }

        public List<string>? Focus { get; set; }

        public string? Instructions { get; set; }

        public RetrievalFile? Retrieval { get; set; }
    }

    private sealed class ModelFile
    {
        public string? Provider { get; set; }

        public string? Name { get; set; }
    }

    private sealed class ReviewFile
    {
        public bool? InlineComments { get; set; }

        public bool? Summary { get; set; }

        public int? MaxFiles { get; set; }

        public int? MaxPatchLines { get; set; }

        public int? ResponseReserveTokens { get; set; }

        public bool? ChunkedReview { get; set; }

        public int? MaxChunks { get; set; }

        public double? ChunkHeadroom { get; set; }
    }

    private sealed class RetrievalFile
    {
        public int? MaxBytes { get; set; }

        public string? SymbolLookupDepth { get; set; }

        public bool? Embeddings { get; set; }
    }
}

public sealed record LiveEvalFixtureResult(
    string FixtureName,
    string ResultPath,
    int CommentCount,
    int RetrievalSnippetCount,
    LlmTokenUsage? TokenUsage);

public sealed record LiveEvalManifest(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    string FixturesDirectory,
    string ResultsDirectory,
    string BaseUrl,
    string Model,
    bool RetrievalEnabled,
    string? ConfigPath,
    int ContextTokens,
    string IndexCacheDir,
    IReadOnlyList<LiveEvalFixtureManifest> Fixtures);

public sealed record LiveEvalFixtureManifest(
    string FixtureKey,
    string FixtureName,
    string Category,
    string ResultPath,
    string Status,
    double ElapsedSeconds,
    int CommentCount,
    int RetrievalSnippetCount,
    int RetrievalSymbolsQueried,
    IReadOnlyList<LiveEvalRetrievalSnippet> RetrievalSnippets,
    LlmTokenUsage? TokenUsage);

public sealed record LiveEvalRetrievalSnippet(
    string Path,
    int StartLine,
    int EndLine,
    int EstimatedTokens,
    string Sha256);

internal sealed record LiveEvalRequestContext(
    ReviewRequest Request,
    IReadOnlyList<LiveEvalRetrievalSnippet> Snippets,
    int SymbolsQueried);
