extern alias apihost;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReviewBot.Core.Domain;
using ReviewBot.GitHub.Auth;
using ReviewBot.GitHub.Config;
using ReviewBot.GitHub.Pulls;
using ReviewBot.Llm.OpenAi;
using ReviewBot.Persistence;

namespace ReviewBot.Evals;

/// <summary>
/// Runs the corpus through the real <c>ReviewWorker</c> instead of reimplementing the pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LiveEvalRunner"/> calls <c>llm.ReviewAsync</c> directly and hand-copies a subset
/// of the worker's pruning, so everything the worker does around the LLM call has never been
/// scored: chunk planning and <c>ReviewResultMerger</c>, <c>FilterCandidateComments</c>,
/// agentic context, verification (<c>FindingCorroborator</c> / <c>FindingRefuter</c>),
/// <c>ApplyOutputConfig</c> and <c>DetermineReviewEvent</c>. This runner boots the actual host
/// and drives it through the webhook, so the number it produces is the product's.
/// </para>
/// <para>
/// Retrieval is off here. The worker's retrieval path git-clones the repository into a
/// workspace, which the fixtures' plain <c>repo-state</c> directories cannot satisfy; until
/// they become clonable repos, the retrieval arm stays with <see cref="LiveEvalRunner"/>.
/// </para>
/// </remarks>
public sealed class WorkerEvalRunner
{
    private const string WebhookSecret = "worker-eval-secret";
    private const string BotSlug = "reviewbot[bot]";
    private const long InstallationId = 98765;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly EvalFixtureLoader loader;

    public WorkerEvalRunner(EvalFixtureLoader? loader = null)
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

        var config = BuildConfig(options);
        var github = new WorkerEvalGitHub();
        var fixtures = Directory
            .EnumerateDirectories(options.FixturesDirectory)
            .Where(directory => File.Exists(Path.Combine(directory, "fixture.yaml")))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var states = new WorkerEvalFixtureState[fixtures.Length];
        for (var index = 0; index < fixtures.Length; index++)
        {
            var prNumber = index + 1;
            var fixture = loader.Load(fixtures[index]);
            var files = EvalDiffParser.ParseFiles(fixture.DiffPatch);
            var repoState = Path.Combine(fixture.DirectoryPath, "repo-state");
            var snapshot = new PullRequestSnapshot(
                Title: fixture.Metadata.PrTitle ?? NeutralPrTitle(files),
                Body: fixture.Metadata.PrBody ?? string.Empty,
                BaseSha: WorkerEvalIdentity.BaseSha(prNumber),
                HeadSha: WorkerEvalIdentity.HeadSha(prNumber),
                Files: files);

            states[index] = new WorkerEvalFixtureState(
                fixture, config, snapshot, Directory.Exists(repoState) ? repoState : null);
            github.Register(prNumber, states[index]);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var databasePath = Path.Combine(
            Path.GetTempPath(), $"reviewbot-worker-eval-{Guid.NewGuid():N}.db");
        await using var factory = new WorkerEvalApplicationFactory(options, github, databasePath);
        using var client = factory.CreateClient();

        var slots = Math.Max(1, options.Concurrency);
        await output.WriteLineAsync(
            $"Running {fixtures.Length} fixtures through ReviewWorker (retrieval=false, concurrency={slots})")
            .ConfigureAwait(false);

        var completed = new (LiveEvalFixtureResult Result, LiveEvalFixtureManifest Manifest)[fixtures.Length];
        var outputLock = new SemaphoreSlim(1, 1);

        await Parallel.ForAsync(
            0,
            fixtures.Length,
            new ParallelOptions { MaxDegreeOfParallelism = slots, CancellationToken = ct },
            async (index, loopCt) =>
            {
                completed[index] = await RunFixtureAsync(
                    client, fixtures[index], index + 1, states[index], options, output, outputLock, loopCt)
                    .ConfigureAwait(false);
            })
            .ConfigureAwait(false);

        var manifest = new LiveEvalManifest(
            StartedAtUtc: startedAt,
            FinishedAtUtc: DateTimeOffset.UtcNow,
            FixturesDirectory: options.FixturesDirectory,
            ResultsDirectory: options.ResultsDirectory,
            BaseUrl: options.BaseUrl.ToString(),
            Model: options.Model,
            RetrievalEnabled: false,
            ConfigPath: options.ConfigPath,
            ContextTokens: options.ContextTokens,
            IndexCacheDir: options.IndexCacheDir,
            Fixtures: completed.Select(entry => entry.Manifest).ToList());
        await File.WriteAllTextAsync(options.ManifestPath, JsonSerializer.Serialize(manifest, JsonOptions), ct)
            .ConfigureAwait(false);

        TryDelete(databasePath);
        TryDelete($"{databasePath}-shm");
        TryDelete($"{databasePath}-wal");

        return completed.Select(entry => entry.Result).ToList();
    }

    private static async Task<(LiveEvalFixtureResult Result, LiveEvalFixtureManifest Manifest)> RunFixtureAsync(
        HttpClient client,
        string fixtureDirectory,
        int prNumber,
        WorkerEvalFixtureState state,
        LiveEvalOptions options,
        TextWriter output,
        SemaphoreSlim outputLock,
        CancellationToken ct)
    {
        var fixtureKey = Path.GetFileName(fixtureDirectory);
        var startedAt = DateTimeOffset.UtcNow;
        ReviewResult result;
        string status;

        try
        {
            var payload = WebhookPayload(prNumber);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/webhook")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-GitHub-Delivery", $"worker-eval-{prNumber}-{Guid.NewGuid():N}");
            request.Headers.Add("X-GitHub-Event", "pull_request");
            request.Headers.Add("X-Hub-Signature-256", Sign(payload));

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Accepted)
            {
                throw new InvalidOperationException(
                    $"webhook returned {(int)response.StatusCode} {response.StatusCode}, expected 202 Accepted");
            }

            result = await state.Posted.Task
                .WaitAsync(TimeSpan.FromSeconds(options.PerFixtureTimeoutSeconds), ct)
                .ConfigureAwait(false);
            status = "succeeded";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var isTimeout = ex is TimeoutException or OperationCanceledException;
            var reason = isTimeout
                ? $"no review posted within {options.PerFixtureTimeoutSeconds}s"
                : $"worker error: {ex.GetBaseException().Message}";
            await WriteLineAsync(output, outputLock, $"FAIL {fixtureKey} ({reason})", ct).ConfigureAwait(false);
            result = new ReviewResult(
                Summary: $"Eval fixture aborted: {reason}.",
                Comments: Array.Empty<InlineComment>(),
                ContextRequests: Array.Empty<ContextRequest>());
            status = isTimeout ? "timed_out" : "errored";
        }

        var elapsed = (DateTimeOffset.UtcNow - startedAt).TotalSeconds;
        var outputPath = Path.Combine(options.ResultsDirectory, $"{fixtureKey}.json");
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(result, JsonOptions), ct)
            .ConfigureAwait(false);
        await WriteLineAsync(
            output,
            outputLock,
            $"{status.ToUpperInvariant()} {fixtureKey} in {elapsed:F0}s (comments={result.Comments.Count})",
            ct).ConfigureAwait(false);

        return (
            new LiveEvalFixtureResult(fixtureKey, outputPath, result.Comments.Count, 0, result.TokenUsage),
            new LiveEvalFixtureManifest(
                FixtureKey: fixtureKey,
                FixtureName: state.Fixture.Metadata.Name,
                Category: state.Fixture.Metadata.Category,
                ResultPath: outputPath,
                Status: status,
                ElapsedSeconds: elapsed,
                CommentCount: result.Comments.Count,
                RetrievalSnippetCount: 0,
                RetrievalSymbolsQueried: 0,
                RetrievalSnippets: [],
                TokenUsage: result.TokenUsage));
    }

    private static ReviewConfig BuildConfig(LiveEvalOptions options) =>
        ReviewConfig.Default with
        {
            Model = new ModelConfig("openai", options.Model),
            Grounding = GroundingConfig.Default with { Enabled = false, Build = false, Tests = false, LocalTests = false },
            Retrieval = RetrievalConfig.Default with { Enabled = false }
        };

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

    private static string WebhookPayload(int prNumber) =>
        // "opened" is what WebhookEndpoint accepts for pull_request events (alongside
        // "reopened"/"synchronize"); "review_requested" is filtered out with 204.
        $$"""
        {
          "action": "opened",
          "installation": { "id": {{InstallationId}} },
          "repository": {
            "name": "{{WorkerEvalIdentity.Repo}}",
            "owner": { "login": "{{WorkerEvalIdentity.Owner}}" }
          },
          "pull_request": {
            "number": {{prNumber}},
            "html_url": "https://github.com/{{WorkerEvalIdentity.Owner}}/{{WorkerEvalIdentity.Repo}}/pull/{{prNumber}}",
            "head": { "sha": "{{WorkerEvalIdentity.HeadSha(prNumber)}}" },
            "user": { "login": "developer" },
            "requested_reviewers": [ { "login": "{{BotSlug}}" } ]
          },
          "requested_reviewer": { "login": "{{BotSlug}}" },
          "sender": { "login": "developer" }
        }
        """;

    private static string Sign(string payload)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), Encoding.UTF8.GetBytes(payload));
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static async Task WriteLineAsync(
        TextWriter output, SemaphoreSlim outputLock, string line, CancellationToken ct)
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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class WorkerEvalApplicationFactory(
        LiveEvalOptions options,
        WorkerEvalGitHub github,
        string databasePath) : WebApplicationFactory<apihost::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // WebApplicationFactory infers the content root from the entry assembly, which for
            // this console CLI resolves to a directory that does not exist. Point it at the
            // host project explicitly.
            builder.UseContentRoot(ResolveApiContentRoot());

            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Webhook:Secret"] = WebhookSecret,
                    ["Webhook:BotSlug"] = BotSlug,
                    ["GitHubApp:AppId"] = "12345",
                    ["GitHubApp:PrivateKeyPem"] = CreatePrivateKeyPem(),
                    ["Persistence:ConnectionString"] = $"Data Source={databasePath}",
                    ["Anthropic:ApiKey"] = "",
                    ["OpenAi:ApiKey"] = options.ApiKey,
                    ["OpenAi:BaseUrl"] = options.BaseUrl.ToString(),
                    ["OpenAi:ModelName"] = options.Model,
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDbContextFactory<ReviewBotDbContext>>();
                services.RemoveAll<DbContextOptions<ReviewBotDbContext>>();
                services.AddDbContextFactory<ReviewBotDbContext>(dbOptions =>
                    dbOptions.UseSqlite(new SqliteConnectionStringBuilder
                    {
                        DataSource = databasePath
                    }.ToString()));

                services.RemoveAll<IInstallationTokenProvider>();
                services.RemoveAll<IRepoConfigFetcher>();
                services.RemoveAll<IPullRequestFetcher>();
                services.RemoveAll<IReviewPoster>();
                services.AddSingleton<IInstallationTokenProvider>(github);
                services.AddSingleton<IRepoConfigFetcher>(github);
                services.AddSingleton<IPullRequestFetcher>(github);
                services.AddSingleton<IReviewPoster>(github);

                // OpenAiLlmOptions is bound eagerly in AddOpenAiReviewLlm during Program.cs
                // startup, before ConfigureAppConfiguration overrides apply — replace it directly.
                services.RemoveAll<OpenAiLlmOptions>();
                services.AddSingleton(new OpenAiLlmOptions
                {
                    ApiKey = options.ApiKey,
                    BaseUrl = options.BaseUrl,
                    ModelName = options.Model,
                    ResponseFormat = "text",
                    Temperature = options.Temperature,
                    Sampling = options.Sampling,
                    MaxTokens = options.MaxTokens,
                    TimeoutSeconds = options.RequestTimeoutSeconds
                });
            });
        }

        /// <summary>
        /// Walks up from the running assembly to the directory holding <c>ReviewBot.sln</c> and
        /// returns the host project beneath it, so the eval works from any working directory.
        /// </summary>
        private static string ResolveApiContentRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ReviewBot.sln")))
                {
                    var contentRoot = Path.Combine(directory.FullName, "src", "ReviewBot.Api");
                    if (Directory.Exists(contentRoot))
                    {
                        return contentRoot;
                    }
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"Could not locate src/ReviewBot.Api by walking up from {AppContext.BaseDirectory}.");
        }

        private static string CreatePrivateKeyPem()
        {
            using var rsa = RSA.Create(2048);
            return rsa.ExportPkcs8PrivateKeyPem();
        }
    }
}
