using System.Diagnostics;
using System.Net;
using DiagActivity = System.Diagnostics.Activity;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using OpenTelemetry.Trace;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ReviewBot.Api.Cost;
using ReviewBot.Api.Tracing;
using ReviewBot.Core.Context;
using NSubstitute;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;
using ReviewBot.GitHub.Auth;
using ReviewBot.GitHub.Checks;
using ReviewBot.GitHub.Config;
using ReviewBot.GitHub.Pulls;
using ReviewBot.Grounding.Build;
using ReviewBot.Persistence;
using ReviewBot.Retrieval;
using ReviewBot.Retrieval.Indexing;
using ReviewBot.Retrieval.Symbols;

namespace ReviewBot.Api.Tests;

public class CompositionRootTests
{
    private const string Secret = "composition-test-secret";
    private const string BotSlug = "reviewbot[bot]";

    [Fact]
    public async Task BuildRunnersForDotNetAndPythonAreRegisteredInDiContainer()
    {
        await using var factory = new ReviewBotApplicationFactory();
        using var scope = factory.Services.CreateScope();

        var runners = scope.ServiceProvider.GetServices<IBuildRunner>().ToList();

        runners.Select(r => r.LanguageId).Should().Contain("dotnet");
        runners.Select(r => r.LanguageId).Should().Contain("python");
    }

    [Fact]
    public async Task TestRunnersForDotNetAndPythonAreRegisteredInDiContainer()
    {
        await using var factory = new ReviewBotApplicationFactory();
        using var scope = factory.Services.CreateScope();

        var runners = scope.ServiceProvider.GetServices<ITestRunner>().ToList();

        runners.Select(r => r.LanguageId).Should().Contain("dotnet");
        runners.Select(r => r.LanguageId).Should().Contain("python");
    }

    [Fact]
    public async Task CheckRunFetcherIsRegisteredInDiContainer()
    {
        await using var factory = new ReviewBotApplicationFactory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICheckRunFetcher>()
            .Should().BeOfType<CheckRunFetcher>();
    }

    [Fact]
    public async Task PromptBudgetingServicesAreRegisteredInDiContainer()
    {
        await using var factory = new ReviewBotApplicationFactory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IModelContextRegistry>()
            .GetContextWindowTokens("gpt-5.1")
            .Should().Be(128_000);
        scope.ServiceProvider.GetRequiredService<IPromptTokenEstimator>()
            .Should().BeOfType<HeuristicTokenEstimator>();
        scope.ServiceProvider.GetRequiredService<IReviewPromptTokenEstimator>()
            .Should().BeOfType<ReviewPromptTokenEstimator>();
        scope.ServiceProvider.GetServices<IProviderPromptTokenEstimator>()
            .Should().ContainSingle(estimator => estimator.ProviderName == "anthropic");
    }

    [Fact]
    public async Task RetrievalServicesAreRegisteredInDiContainer()
    {
        await using var factory = new ReviewBotApplicationFactory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IDiffSymbolExtractor>()
            .Should().BeOfType<CSharpDiffSymbolExtractor>();
        scope.ServiceProvider.GetRequiredService<IRepoIndexFactory>()
            .Should().BeOfType<SqliteRepoIndexFactory>();
        scope.ServiceProvider.GetServices<IRepoSymbolParser>()
            .Should().ContainSingle(parser => parser.GetType() == typeof(CSharpRepoSymbolParser));
        scope.ServiceProvider.GetRequiredService<IRetrievalProvider>()
            .Should().BeOfType<SqliteRetrievalProvider>();
        factory.Services.GetServices<IHostedService>()
            .Should().Contain(service => service.GetType() == typeof(RepoIndexCleanupService));
    }

    [Fact]
    public async Task TracingServicesAreRegisteredInDiContainer()
    {
        await using var factory = new ReviewBotApplicationFactory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IReviewTraceWriter>()
            .Should().BeOfType<JsonReviewTraceWriter>();
        factory.Services.GetServices<IHostedService>()
            .Should().Contain(service => service.GetType() == typeof(TraceCleanupService));
    }

    [Fact]
    public async Task OtelTracerProviderIsRegisteredInDiContainer()
    {
        await using var factory = new ReviewBotApplicationFactory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<TracerProvider>()
            .Should().NotBeNull();
    }

    [Fact]
    public async Task OtelReviewBotSourceEmitsActivitiesWhenListenerIsAttached()
    {
        var started = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "ReviewBot",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a => { lock (started) started.Add(a.OperationName); }
        };
        ActivitySource.AddActivityListener(listener);

        await using var factory = new ReviewBotApplicationFactory();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TracerProvider>().Should().NotBeNull();

        using var src = new ActivitySource("ReviewBot", "1.0.0");
        using DiagActivity? act = src.StartActivity("reviewbot.test_probe");

        lock (started) started.Should().Contain("reviewbot.test_probe");
    }

    [Fact]
    public async Task CostCalculatorIsRegisteredInDiContainer()
    {
        await using var factory = new ReviewBotApplicationFactory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IReviewCostCalculator>()
            .Should().BeOfType<ReviewCostCalculator>();
    }

    [Fact]
    public async Task HealthzReturnsOkAfterApplyingMigrations()
    {
        await using var factory = new ReviewBotApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SignedWebhookRunsWorkerPipelineAndPostsStubReview()
    {
        var tokenProvider = Substitute.For<IInstallationTokenProvider>();
        var repoConfigFetcher = Substitute.For<IRepoConfigFetcher>();
        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        var llmFactory = Substitute.For<IReviewLlmFactory>();
        var reviewPoster = Substitute.For<IReviewPoster>();
        var posted = new TaskCompletionSource<ReviewResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stubResult = new ReviewResult("Stub summary from the LLM.", []);

        tokenProvider.GetTokenAsync(98765, Arg.Any<CancellationToken>())
            .Returns(new InstallationToken("install-token", DateTimeOffset.UtcNow.AddHours(1)));
        // This test exercises the webhook -> worker -> post pipeline, not retrieval
        // (which now defaults on and would clone/index the repo), so opt out of it.
        repoConfigFetcher.FetchAsync("octo-org", "reviewbot", "head-sha-abc", "install-token", Arg.Any<CancellationToken>())
            .Returns(ReviewConfig.Default with
            {
                Retrieval = ReviewConfig.Default.Retrieval with { Enabled = false }
            });
        pullRequestFetcher.FetchMetadataAsync("octo-org", "reviewbot", 42, "install-token", Arg.Any<CancellationToken>())
            .Returns(new PullRequestMetadata("Improve parser", "Adds coverage.", "base-sha", "head-sha-abc"));
        pullRequestFetcher.FetchFilesAsync("octo-org", "reviewbot", 42, "install-token", 50, Arg.Any<IReadOnlySet<string>?>(), Arg.Any<CancellationToken>())
            .Returns([CreateFile("src/App.cs")]);
        llmFactory.Create(Arg.Any<ModelConfig>())
            .Returns(new StubReviewLlm(stubResult));
        reviewPoster.PostAsync(
                "octo-org",
                "reviewbot",
                42,
                "head-sha-abc",
                Arg.Any<ReviewResult>(),
                Arg.Any<IReadOnlyList<FileChange>>(),
                "install-token",
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                posted.SetResult(call.ArgAt<ReviewResult>(4));
                return Task.CompletedTask;
            });

        await using var factory = new ReviewBotApplicationFactory(services =>
        {
            services.RemoveAll<IInstallationTokenProvider>();
            services.RemoveAll<IRepoConfigFetcher>();
            services.RemoveAll<IPullRequestFetcher>();
            services.RemoveAll<IReviewLlmFactory>();
            services.RemoveAll<IReviewPoster>();

            services.AddSingleton(tokenProvider);
            services.AddSingleton(repoConfigFetcher);
            services.AddSingleton(pullRequestFetcher);
            services.AddSingleton(llmFactory);
            services.AddSingleton(reviewPoster);
        });
        using var client = factory.CreateClient();
        using var request = CreateWebhookRequest(CreatePullRequestPayload());

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var postedResult = await posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The summary is synthesized from findings, not the model's free-text; a clean
        // review posts just the re-review hint.
        postedResult.Summary.Should().NotContain(stubResult.Summary);
        postedResult.Summary.Should().Contain("/review");
    }

    private static FileChange CreateFile(string path) =>
        new(
            path,
            "@@ -1,2 +1,2 @@\n line\n+added",
            new HashSet<int> { 1, 2 },
            AdditionsCount: 1,
            DeletionsCount: 0,
            FileChangeStatus.Modified);

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

    private static string Sign(string payload)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(payload));
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static string CreatePullRequestPayload() =>
        $$"""
        {
          "action": "opened",
          "installation": {
            "id": 98765
          },
          "repository": {
            "name": "reviewbot",
            "owner": {
              "login": "octo-org"
            }
          },
          "pull_request": {
            "number": 42,
            "html_url": "https://github.com/octo-org/reviewbot/pull/42",
            "head": {
              "sha": "head-sha-abc"
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

    private sealed class ReviewBotApplicationFactory(
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
                    ["Webhook:Secret"] = Secret,
                    ["Webhook:BotSlug"] = BotSlug,
                    ["GitHubApp:AppId"] = "12345",
                    ["GitHubApp:PrivateKeyPem"] = "test-private-key-placeholder",
                    ["Persistence:ConnectionString"] = "Data Source=composition-test.db",
                    ["Anthropic:ApiKey"] = "",
                    ["OpenAi:ApiKey"] = "",
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
            {
                connection.Dispose();
            }
        }
    }
}
