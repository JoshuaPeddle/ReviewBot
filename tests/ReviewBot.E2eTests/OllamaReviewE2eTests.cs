using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using ReviewBot.Core.Domain;
using ReviewBot.GitHub.Auth;
using ReviewBot.GitHub.Config;
using ReviewBot.GitHub.Pulls;
using ReviewBot.Llm.OpenAi;
using ReviewBot.Persistence;

namespace ReviewBot.E2eTests;

/// <summary>
/// End-to-end tests that validate the full PR review pipeline using a locally running
/// <a href="https://ollama.com">Ollama</a> instance as the LLM backend via the OpenAI-compatible API.
///
/// <para>
/// These tests are skipped automatically when the required environment variables are not set
/// or when Ollama is not reachable, so they are safe to keep in CI as long as no Ollama
/// server is configured there.
/// </para>
///
/// <para>
/// Required environment variables:
/// <list type="bullet">
///   <item><description><c>REVIEWBOT_E2E_OLLAMA_MODEL</c> – name of an installed Ollama model (e.g. <c>llama3</c>).</description></item>
///   <item><description><c>REVIEWBOT_E2E_OLLAMA_URL</c> – (optional) base URL of the Ollama OpenAI-compatible endpoint.
///     Defaults to <c>http://localhost:11434/v1</c>.</description></item>
/// </list>
/// </para>
/// </summary>
public class OllamaReviewE2eTests
{
    private const string WebhookSecret = "e2e-test-secret";
    private const string BotSlug = "reviewbot[bot]";
    private const string Owner = "octo-org";
    private const string Repo = "reviewbot";
    private const int PrNumber = 42;
    private const long InstallationId = 98765;
    private const string HeadSha = "head-sha-e2e";

    private static readonly string? OllamaBaseUrl =
        Environment.GetEnvironmentVariable("REVIEWBOT_E2E_OLLAMA_URL") ?? "http://localhost:11434/v1";

    private static readonly string? OllamaModel =
        Environment.GetEnvironmentVariable("REVIEWBOT_E2E_OLLAMA_MODEL");

    /// <summary>
    /// Verifies that the end-to-end review pipeline — from receiving a GitHub webhook through
    /// LLM invocation to posting a review — produces a non-trivial, non-empty review summary
    /// when backed by a real Ollama model.
    /// </summary>
    /// <remarks>
    /// The test is automatically skipped when:
    /// <list type="bullet">
    ///   <item><description><c>REVIEWBOT_E2E_OLLAMA_MODEL</c> is not set.</description></item>
    ///   <item><description>Ollama is not reachable at the configured base URL.</description></item>
    /// </list>
    /// GitHub API interactions (token provisioning, config fetching, PR fetching, and review posting)
    /// are replaced with NSubstitute fakes so no real GitHub credentials are required.
    /// </remarks>
    [SkippableFact]
    public async Task PipelineProducesValidReviewViaOllama()
    {
        Skip.If(OllamaModel is null, "Set REVIEWBOT_E2E_OLLAMA_MODEL to an installed model name to run this test.");
        var reachable = await IsOllamaReachableAsync();
        Skip.If(!reachable, $"Ollama not reachable at {OllamaBaseUrl}");

        var tokenProvider = Substitute.For<IInstallationTokenProvider>();
        var repoConfigFetcher = Substitute.For<IRepoConfigFetcher>();
        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        var reviewPoster = Substitute.For<IReviewPoster>();
        var posted = new TaskCompletionSource<ReviewResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var ollamaConfig = ReviewConfig.Default with
        {
            Model = new ModelConfig("openai", OllamaModel!, null),
            Grounding = GroundingConfig.Default with { Enabled = false }
        };

        tokenProvider.GetTokenAsync(InstallationId, Arg.Any<CancellationToken>())
            .Returns(new InstallationToken("fake-install-token", DateTimeOffset.UtcNow.AddHours(1)));

        repoConfigFetcher.FetchAsync(Owner, Repo, HeadSha, "fake-install-token", Arg.Any<CancellationToken>())
            .Returns(ollamaConfig);

        pullRequestFetcher.FetchAsync(Owner, Repo, PrNumber, "fake-install-token", 50, Arg.Any<CancellationToken>())
            .Returns(new PullRequestSnapshot(
                "Add UserService with repository lookup",
                "Introduces a UserService class for looking up and displaying user information.",
                "base-sha-001",
                HeadSha,
                [CreateUserServiceFile()]));

        reviewPoster.PostAsync(
                Owner, Repo, PrNumber, HeadSha,
                Arg.Any<ReviewResult>(),
                Arg.Any<IReadOnlyList<FileChange>>(),
                "fake-install-token",
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                posted.TrySetResult(call.ArgAt<ReviewResult>(4));
                return Task.CompletedTask;
            });

        await using var factory = new OllamaApplicationFactory(services =>
        {
            services.RemoveAll<IInstallationTokenProvider>();
            services.RemoveAll<IRepoConfigFetcher>();
            services.RemoveAll<IPullRequestFetcher>();
            services.RemoveAll<IReviewPoster>();

            services.AddSingleton(tokenProvider);
            services.AddSingleton(repoConfigFetcher);
            services.AddSingleton(pullRequestFetcher);
            services.AddSingleton(reviewPoster);

            // OpenAiLlmOptions is bound eagerly during Program.cs startup, before
            // ConfigureAppConfiguration test overrides apply — replace it directly.
            services.RemoveAll<OpenAiLlmOptions>();
            services.AddSingleton(new OpenAiLlmOptions
            {
                ApiKey = "ollama",
                BaseUrl = new Uri(OllamaBaseUrl!),
                ModelName = OllamaModel!,
                UseJsonMode = true,
            });
        });

        using var client = factory.CreateClient();
        var payload = CreatePullRequestPayload();
        using var request = CreateWebhookRequest(payload);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var result = await posted.Task.WaitAsync(TimeSpan.FromSeconds(120));

        result.Summary.Should().NotBeNullOrWhiteSpace("LLM should produce a non-empty review summary");
        result.Summary.Length.Should().BeGreaterThan(20, "expected a substantive summary, not a one-word response");
    }

    /// <summary>
    /// Performs a lightweight connectivity check against the Ollama server by calling the
    /// <c>/api/tags</c> endpoint with a short timeout.
    /// </summary>
    /// <returns><see langword="true"/> if Ollama responded with a success status code; otherwise <see langword="false"/>.</returns>
    private static async Task<bool> IsOllamaReachableAsync()
    {
        try
        {
            var uri = new Uri(OllamaBaseUrl!);
            var tagsUri = new Uri($"{uri.Scheme}://{uri.Host}:{uri.Port}/api/tags");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(tagsUri);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Builds a synthetic <see cref="FileChange"/> that represents a newly added
    /// <c>UserService</c> class. The diff intentionally contains several code-quality
    /// issues (null-unsafe member access, magic strings, console logging) so the LLM has
    /// meaningful content to comment on.
    /// </summary>
    /// <returns>A <see cref="FileChange"/> describing 30 added lines in <c>src/Services/UserService.cs</c>.</returns>
    private static FileChange CreateUserServiceFile() =>
        new(
            "src/Services/UserService.cs",
            "@@ -0,0 +1,30 @@\n" +
            "+public class UserService\n" +
            "+{\n" +
            "+    private readonly IUserRepository _repository;\n" +
            "+\n" +
            "+    public UserService(IUserRepository repository)\n" +
            "+    {\n" +
            "+        _repository = repository;\n" +
            "+    }\n" +
            "+\n" +
            "+    public string GetDisplayName(int userId)\n" +
            "+    {\n" +
            "+        var user = _repository.FindById(userId);\n" +
            "+        return user.FirstName + \" \" + user.LastName;\n" +
            "+    }\n" +
            "+\n" +
            "+    public bool IsAdmin(int userId)\n" +
            "+    {\n" +
            "+        var user = _repository.FindById(userId);\n" +
            "+        if (user.Role == \"admin\") return true;\n" +
            "+        return false;\n" +
            "+    }\n" +
            "+\n" +
            "+    public void DeleteUser(int userId)\n" +
            "+    {\n" +
            "+        var user = _repository.FindById(userId);\n" +
            "+        _repository.Delete(user);\n" +
            "+        Console.WriteLine(\"Deleted: \" + userId);\n" +
            "+    }\n" +
            "+}",
            new HashSet<int>(Enumerable.Range(1, 30)),
            AdditionsCount: 30,
            DeletionsCount: 0,
            FileChangeStatus.Added);

    /// <summary>
    /// Creates a JSON payload that mimics a GitHub <c>pull_request</c> webhook event with
    /// action <c>review_requested</c>, targeting the bot as the requested reviewer.
    /// </summary>
    /// <returns>A JSON string suitable for use as the body of a <c>POST /webhook</c> request.</returns>
    private static string CreatePullRequestPayload() =>
        $$"""
        {
          "action": "review_requested",
          "installation": {
            "id": {{InstallationId}}
          },
          "repository": {
            "name": "{{Repo}}",
            "owner": {
              "login": "{{Owner}}"
            }
          },
          "pull_request": {
            "number": {{PrNumber}},
            "html_url": "https://github.com/{{Owner}}/{{Repo}}/pull/{{PrNumber}}",
            "head": {
              "sha": "{{HeadSha}}"
            },
            "user": {
              "login": "developer"
            },
            "requested_reviewers": [
              {
                "login": "{{BotSlug}}"
              }
            ]
          },
          "requested_reviewer": {
            "login": "{{BotSlug}}"
          },
          "sender": {
            "login": "developer"
          }
        }
        """;

    /// <summary>
    /// Builds an <see cref="HttpRequestMessage"/> that represents a signed GitHub webhook
    /// delivery for a <c>pull_request</c> event, including the required
    /// <c>X-GitHub-Delivery</c>, <c>X-GitHub-Event</c>, and <c>X-Hub-Signature-256</c> headers.
    /// </summary>
    /// <param name="payload">The raw JSON payload to include in the request body.</param>
    /// <returns>A signed <see cref="HttpRequestMessage"/> targeting <c>POST /webhook</c>.</returns>
    private static HttpRequestMessage CreateWebhookRequest(string payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhook")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-GitHub-Delivery", $"delivery-{Guid.NewGuid():N}");
        request.Headers.Add("X-GitHub-Event", "pull_request");
        request.Headers.Add("X-Hub-Signature-256", Sign(payload));
        return request;
    }

    /// <summary>
    /// Computes an HMAC-SHA256 signature over <paramref name="payload"/> using
    /// <see cref="WebhookSecret"/> and formats it as the value expected by the
    /// <c>X-Hub-Signature-256</c> GitHub header (<c>sha256=&lt;hex&gt;</c>).
    /// </summary>
    /// <param name="payload">The raw JSON payload to sign.</param>
    /// <returns>The formatted HMAC-SHA256 signature string.</returns>
    private static string Sign(string payload)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), Encoding.UTF8.GetBytes(payload));
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    /// <summary>
    /// A <see cref="WebApplicationFactory{TEntryPoint}"/> that boots the full ReviewBot API
    /// in-process and configures it to use:
    /// <list type="bullet">
    ///   <item><description>An in-memory SQLite database (shared connection) for persistence.</description></item>
    ///   <item><description>The Ollama OpenAI-compatible endpoint as the LLM backend.</description></item>
    ///   <item><description>Minimal placeholder values for secrets that are not exercised by the test.</description></item>
    /// </list>
    /// Additional service overrides can be supplied via the <paramref name="configureTestServices"/> callback.
    /// </summary>
    /// <param name="configureTestServices">
    /// An optional callback invoked after the factory's own service overrides have been applied,
    /// allowing individual tests to replace further services (e.g. GitHub API fakes).
    /// </param>
    private sealed class OllamaApplicationFactory(
        Action<IServiceCollection>? configureTestServices = null) : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection connection = new("DataSource=:memory:");
        private bool connectionOpened;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Webhook:Secret"] = WebhookSecret,
                    ["Webhook:BotSlug"] = BotSlug,
                    ["GitHubApp:AppId"] = "12345",
                    ["GitHubApp:PrivateKeyPem"] = "test-private-key-placeholder",
                    ["Persistence:ConnectionString"] = "Data Source=:memory:",
                    ["Anthropic:ApiKey"] = "",
                    ["OpenAi:ApiKey"] = "ollama",
                    ["OpenAi:BaseUrl"] = OllamaBaseUrl,
                    ["OpenAi:ModelName"] = OllamaModel,
                });
            });

            builder.ConfigureTestServices(services =>
            {
                if (!connectionOpened)
                {
                    connection.Open();
                    connectionOpened = true;
                }

                services.RemoveAll<IDbContextFactory<ReviewBotDbContext>>();
                services.RemoveAll<DbContextOptions<ReviewBotDbContext>>();
                services.AddDbContextFactory<ReviewBotDbContext>(options => options.UseSqlite(connection));

                configureTestServices?.Invoke(services);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                connection.Dispose();
        }
    }
}
