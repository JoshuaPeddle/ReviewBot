using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewBot.Core.Context;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;
using ReviewBot.Core.Prompting;
using ReviewBot.Llm.OpenAi;
using ReviewBot.Retrieval;
using ReviewBot.Retrieval.Indexing;
using ReviewBot.Grounding.Languages.DotNet;
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
                Sampling = options.Sampling,
                MaxTokens = options.MaxTokens,
                TimeoutSeconds = options.RequestTimeoutSeconds
            },
            NullLogger<OpenAiReviewLlm>.Instance);
        var fixtures = Directory
            .EnumerateDirectories(options.FixturesDirectory)
            .Where(directory => File.Exists(Path.Combine(directory, "fixture.yaml")))
            .Order(StringComparer.Ordinal)
            .ToArray();
        // Request building indexes each fixture's repo-state into one SQLite index shared
        // across the run, so it stays sequential. It is local disk work and rounds to nothing
        // beside the LLM call — only the LLM stage below is worth fanning out.
        var prepared = new (string Directory, EvalFixture Fixture, LiveEvalRequestContext Context)[fixtures.Length];
        for (var index = 0; index < fixtures.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var fixture = loader.Load(fixtures[index]);
            prepared[index] = (
                fixtures[index],
                fixture,
                await BuildRequestAsync(fixture, config, options, ct).ConfigureAwait(false));
        }

        var slots = Math.Max(1, options.Concurrency);
        await output.WriteLineAsync(
            $"Running {prepared.Length} fixtures (retrieval={options.RetrievalEnabled.ToString().ToLowerInvariant()}, concurrency={slots})")
            .ConfigureAwait(false);

        // Results are collected by index and assembled in fixture order afterwards, so the
        // manifest and the score are identical whatever order the requests finish in.
        var completed = new (LiveEvalFixtureResult Result, LiveEvalFixtureManifest Manifest)[prepared.Length];
        using var outputLock = new SemaphoreSlim(1, 1);

        await Parallel.ForAsync(
            0,
            prepared.Length,
            new ParallelOptions { MaxDegreeOfParallelism = slots, CancellationToken = ct },
            async (index, loopCt) =>
            {
                completed[index] = await RunFixtureAsync(
                    llm, prepared[index], config, options, output, outputLock, loopCt).ConfigureAwait(false);
            })
            .ConfigureAwait(false);

        var results = completed.Select(entry => entry.Result).ToList();
        var manifestFixtures = completed.Select(entry => entry.Manifest).ToList();

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

    /// <summary>
    /// Runs one fixture through the LLM (plus the noise filters, when enabled) and writes its
    /// result file. Safe to call concurrently: every argument is either immutable or owned by
    /// this call, and <see cref="OpenAiReviewLlm"/> builds a fresh client per request.
    /// </summary>
    private static async Task<(LiveEvalFixtureResult Result, LiveEvalFixtureManifest Manifest)> RunFixtureAsync(
        IReviewLlm llm,
        (string Directory, EvalFixture Fixture, LiveEvalRequestContext Context) prepared,
        ReviewConfig config,
        LiveEvalOptions options,
        TextWriter output,
        SemaphoreSlim outputLock,
        CancellationToken ct)
    {
        var (fixtureDirectory, fixture, requestContext) = prepared;
        var fixtureKey = Path.GetFileName(fixtureDirectory);
        var fixtureStartedAt = DateTimeOffset.UtcNow;
        ReviewResult result;
        string status;

        using (var fixtureCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            fixtureCts.CancelAfter(TimeSpan.FromSeconds(options.PerFixtureTimeoutSeconds));
            try
            {
                result = options.EnsembleSamples > 1
                    ? await ReviewWithEnsembleAsync(
                        llm, requestContext.Request, fixtureKey, options, fixtureCts.Token).ConfigureAwait(false)
                    : await llm.ReviewAsync(requestContext.Request, fixtureCts.Token).ConfigureAwait(false);

                // Critique runs on the merged consensus, not on each sample: the product
                // critiques one review, and critiquing k times would confound the two effects.
                if (options.SelfCritique)
                {
                    // Mirror the worker's precision-pruning stages (MinConfidence gate +
                    // the self-critique LLM pass) so the corpus measures real product
                    // precision, not raw model output.
                    result = await ApplyNoiseFiltersAsync(
                        llm, requestContext.Request, result, config, fixtureCts.Token).ConfigureAwait(false);
                }

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
                await WriteLineAsync(
                    output,
                    outputLock,
                    $"FAIL {fixtureKey} after {elapsedSeconds:F0}s ({reason}); writing empty result and continuing.",
                    ct).ConfigureAwait(false);
                result = new ReviewResult(
                    Summary: $"Eval fixture aborted: {reason}.",
                    Comments: Array.Empty<InlineComment>(),
                    ContextRequests: Array.Empty<ContextRequest>());
                status = isTimeout ? "timed_out" : "errored";
            }
        }

        var elapsed = (DateTimeOffset.UtcNow - fixtureStartedAt).TotalSeconds;
        var outputPath = Path.Combine(options.ResultsDirectory, $"{fixtureKey}.json");
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(result, JsonOptions), ct).ConfigureAwait(false);
        await WriteLineAsync(
            output,
            outputLock,
            $"{status.ToUpperInvariant()} {fixtureKey} in {elapsed:F0}s (comments={result.Comments.Count}, snippets={requestContext.Snippets.Count})",
            ct).ConfigureAwait(false);

        return (
            new LiveEvalFixtureResult(
                fixtureKey,
                outputPath,
                result.Comments.Count,
                requestContext.Snippets.Count,
                result.TokenUsage),
            new LiveEvalFixtureManifest(
                FixtureKey: fixtureKey,
                FixtureName: fixture.Metadata.Name,
                Category: fixture.Metadata.Category,
                ResultPath: outputPath,
                Status: status,
                ElapsedSeconds: elapsed,
                CommentCount: result.Comments.Count,
                RetrievalSnippetCount: requestContext.Snippets.Count,
                RetrievalSymbolsQueried: requestContext.SymbolsQueried,
                RetrievalSnippets: requestContext.Snippets,
                TokenUsage: result.TokenUsage));
    }

    /// <summary>
    /// Samples the reviewer k times, persists every sample, and returns the merged consensus.
    /// </summary>
    /// <remarks>
    /// The samples are written to <c>&lt;fixture&gt;.samples.json</c> so a threshold sweep costs
    /// one run rather than one run per threshold — re-merge them with the
    /// <c>ensemble-rescore</c> verb. A sample that throws is dropped; k-1 samples still merge.
    /// </remarks>
    private static async Task<ReviewResult> ReviewWithEnsembleAsync(
        IReviewLlm llm,
        ReviewRequest request,
        string fixtureKey,
        LiveEvalOptions options,
        CancellationToken ct)
    {
        var samples = new ReviewResult?[options.EnsembleSamples];
        var failures = new Exception?[options.EnsembleSamples];
        await Parallel.ForAsync(
            0,
            options.EnsembleSamples,
            new ParallelOptions
            {
                // Fixtures already run concurrently, so sampling k times per fixture multiplies
                // load; honouring the provider's own limit keeps that product bounded.
                MaxDegreeOfParallelism = Math.Max(1, Math.Min(options.EnsembleSamples, llm.MaxConcurrentRequests)),
                CancellationToken = ct
            },
            async (index, loopCt) =>
            {
                try
                {
                    samples[index] = await llm.ReviewAsync(request, loopCt).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    samples[index] = null;
                    failures[index] = ex;
                }
            })
            .ConfigureAwait(false);

        var succeeded = samples.OfType<ReviewResult>().ToArray();
        if (succeeded.Length == 0)
        {
            // Same reasoning as EnsembleReviewLlm: a bare count is undiagnosable, and the cause
            // is what tells you whether to shrink the request or fix the endpoint.
            var observed = failures.OfType<Exception>().ToArray();
            var distinct = observed
                .Select(failure => failure.GetBaseException().Message)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            throw new InvalidOperationException(
                $"All {options.EnsembleSamples} ensemble samples failed for {fixtureKey}. " +
                $"Distinct cause(s): {string.Join(" | ", distinct)}",
                observed.FirstOrDefault());
        }

        var samplesPath = Path.Combine(options.ResultsDirectory, $"{fixtureKey}.samples.json");
        await File.WriteAllTextAsync(samplesPath, JsonSerializer.Serialize(succeeded, JsonOptions), ct)
            .ConfigureAwait(false);

        return EnsembleMerger.Merge(
            succeeded,
            Math.Min(options.EnsembleMinAgreement, succeeded.Length),
            options.EnsembleLineWindow).Result;
    }

    // TextWriter is not thread-safe, so progress lines from concurrent fixtures are
    // serialized rather than allowed to interleave mid-line.
    private static async Task WriteLineAsync(
        TextWriter output,
        SemaphoreSlim outputLock,
        string line,
        CancellationToken ct)
    {
        await outputLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await output.WriteLineAsync(line).ConfigureAwait(false);
        }
        finally
        {
            outputLock.Release();
        }
    }

    // Reproduces the worker's noise-pruning: the deterministic MinConfidence gate followed
    // by the self-critique LLM pass over every surviving comment. The critique is handed
    // the same repository context and full-file content the review pass saw, exactly as
    // the worker does — without it the critic deletes findings that reason across files.
    private static async Task<ReviewResult> ApplyNoiseFiltersAsync(
        IReviewLlm llm,
        ReviewRequest request,
        ReviewResult result,
        ReviewConfig config,
        CancellationToken ct)
    {
        var candidates = result.Comments
            .Where(comment => comment.Confidence >= config.Review.MinConfidence)
            .ToArray();
        if (candidates.Length == 0)
        {
            return result with { Comments = candidates };
        }

        try
        {
            var payload = SelfCritiquePromptBuilder.Build(
                request.Files,
                candidates,
                request.RepositoryContext,
                request.FullFileContents,
                config.Review.MaxPatchLines);
            var rawCritique = await llm.CompleteRawAsync(payload, ct, "self_critique").ConfigureAwait(false);
            var critique = SelfCritiqueParser.Parse(rawCritique, candidates.Length);
            if (critique is null)
            {
                return result with { Comments = candidates };
            }

            return result with
            {
                Comments = critique.RetainedIndices.Select(index => candidates[index]).ToArray()
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return result with { Comments = candidates };
        }
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

    /// <summary>
    /// A PR title that says what changed and nothing about whether it is wrong — the kind of
    /// title a real PR carries. Anything richer risks re-introducing the priming this replaced.
    /// </summary>
    private static string NeutralPrTitle(IReadOnlyList<FileChange> files)
    {
        if (files.Count == 0)
        {
            return "Update repository";
        }

        var first = Path.GetFileName(files[0].Path);
        return files.Count == 1
            ? $"Update {first}"
            : $"Update {first} and {files.Count - 1} other file{(files.Count == 2 ? string.Empty : "s")}";
    }

    private static async Task<LiveEvalRequestContext> BuildRequestAsync(
        EvalFixture fixture,
        ReviewConfig config,
        LiveEvalOptions options,
        CancellationToken ct)
    {
        var files = EvalDiffParser.ParseFiles(fixture.DiffPatch);
        // The fixture's name and description are the answer key ("...leaks secret state",
        // "the reviewer should flag this as a trust-boundary leak"). Sending them as the PR
        // title/body primes the model and inflates recall by an unmeasured margin, so the
        // model gets a neutral title derived from the changed paths unless the fixture
        // supplies a deliberately neutral pr_title/pr_body of its own.
        var request = new ReviewRequest(
            PrTitle: fixture.Metadata.PrTitle ?? NeutralPrTitle(files),
            PrBody: fixture.Metadata.PrBody ?? string.Empty,
            BaseSha: BaseSha,
            HeadSha: HeadSha,
            Files: files,
            Config: config);

        var repoState = Path.Combine(fixture.DirectoryPath, "repo-state");
        var hasRepoState = Directory.Exists(repoState);

        // Full-file context is the largest section of a real review prompt, so a corpus
        // that omits it measures a prompt the product never sends. repo-state is the head
        // state of the fixture repo, which is exactly what production fetches from GitHub.
        if (hasRepoState)
        {
            var fullFileContents = ReadFullFileContents(files, repoState, config);
            if (fullFileContents.Count > 0)
            {
                request = request with { FullFileContents = fullFileContents };
            }

            // Same compiler-settled facts the worker states, from the same extractor, so
            // the corpus measures the prompt the product actually sends.
            var languageFacts = ExtractLanguageFacts(files, repoState);
            if (languageFacts.Count > 0)
            {
                request = request with { LanguageFacts = languageFacts };
            }
        }

        if (!options.RetrievalEnabled || !hasRepoState)
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

    /// <summary>
    /// Reads the head content of changed files that qualify for full-file context.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="FullFileContextSelector"/> — the same predicates the worker applies —
    /// so the corpus exercises the shipped selection rather than a lookalike. Files absent
    /// from repo-state are skipped, which mirrors production treating a failed fetch as
    /// "continue with the diff alone".
    /// </remarks>
    private static IReadOnlyDictionary<string, string> ReadFullFileContents(
        IReadOnlyList<FileChange> files,
        string repoStateDirectory,
        ReviewConfig config)
    {
        var contents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in FullFileContextSelector.SelectCandidates(files, config.Review.FullFileMaxBytes))
        {
            var path = Path.Combine(repoStateDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                contents[file.Path] = File.ReadAllText(path);
            }
        }

        return contents;
    }

    private static IReadOnlyList<LanguageFact> ExtractLanguageFacts(
        IReadOnlyList<FileChange> files,
        string repoStateDirectory)
    {
        var facts = new List<LanguageFact>();
        foreach (var file in files.Where(file => file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var path = Path.Combine(repoStateDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                continue;
            }

            facts.AddRange(RoslynLiteralFactExtractor.Extract(
                file.Path, File.ReadAllText(path), file.CommentableLines));
        }

        return facts;
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
            Model = new ModelConfig("openai", options.Model),
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

        public int? MaxChunks { get; set; }

        public double? ChunkHeadroom { get; set; }
    }

    private sealed class RetrievalFile
    {
        public int? MaxBytes { get; set; }

        public string? SymbolLookupDepth { get; set; }
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
