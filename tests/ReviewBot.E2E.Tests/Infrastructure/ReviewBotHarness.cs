using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReviewBot.Api.Webhooks;
using ReviewBot.GitHub.Auth;
using ReviewBot.Llm.Anthropic;
using ReviewBot.Llm.OpenAi;
using ReviewBot.Persistence;
using WireMock.Server;

namespace ReviewBot.E2E.Tests.Infrastructure;

public sealed class ReviewBotHarness : IAsyncLifetime
{
    public const string WebhookSecret = "test-secret";
    public const string BotSlug = "reviewbot[bot]";

    private readonly string databasePath = Path.Combine(
        Path.GetTempPath(),
        $"reviewbot-e2e-{Guid.NewGuid():N}.db");
    private readonly string privateKeyPem;
    private readonly WebApplicationFactory<Program> factory;

    public ReviewBotHarness()
    {
        privateKeyPem = CreatePrivateKeyPem();
        GitHubMock = WireMockServer.Start();
        LlmMock = WireMockServer.Start();
        factory = new HarnessApplicationFactory(this);
    }

    public WireMockServer GitHubMock { get; }

    public WireMockServer LlmMock { get; }

    public HttpClient CreateClient() => factory.CreateClient();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task ResetAsync()
    {
        GitHubMock.Reset();
        LlmMock.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        factory.Dispose();
        GitHubMock.Stop();
        LlmMock.Stop();
        GitHubMock.Dispose();
        LlmMock.Dispose();

        TryDelete(databasePath);
        TryDelete($"{databasePath}-shm");
        TryDelete($"{databasePath}-wal");
        return Task.CompletedTask;
    }

    private Dictionary<string, string?> CreateConfiguration() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Webhook:Secret"] = WebhookSecret,
        ["Webhook:BotSlug"] = BotSlug,
        ["GitHubApp:AppId"] = "12345",
        ["GitHubApp:PrivateKeyPem"] = privateKeyPem,
        ["GitHubApp:ApiBaseUrl"] = GitHubMock.Url,
        ["Persistence:ConnectionString"] = $"Data Source={databasePath}",
        ["Anthropic:ApiKey"] = "e2e-anthropic-key",
        ["OpenAi:ApiKey"] = "e2e-openai-key",
        ["OpenAi:BaseUrl"] = $"{LlmMock.Url}/v1",
        ["OpenAi:ModelName"] = "e2e-openai-model",
    };

    private static string CreatePrivateKeyPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
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

    private sealed class HarnessApplicationFactory(ReviewBotHarness harness) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(harness.CreateConfiguration());
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDbContextFactory<ReviewBotDbContext>>();
                services.RemoveAll<DbContextOptions<ReviewBotDbContext>>();
                services.AddDbContextFactory<ReviewBotDbContext>(options =>
                    options.UseSqlite(new SqliteConnectionStringBuilder
                    {
                        DataSource = harness.databasePath
                    }.ToString()));

                services.Configure<WebhookOptions>(options =>
                {
                    options.Secret = WebhookSecret;
                    options.BotSlug = BotSlug;
                });
                services.Configure<GitHubAppOptions>(options =>
                {
                    options.AppId = 12345;
                    options.PrivateKeyPem = harness.privateKeyPem;
                    options.ApiBaseUrl = new Uri(harness.GitHubMock.Url!);
                });

                services.RemoveAll<AnthropicLlmOptions>();
                services.AddSingleton(new AnthropicLlmOptions
                {
                    ApiKey = "e2e-anthropic-key"
                });

                services.RemoveAll<OpenAiLlmOptions>();
                services.AddSingleton(new OpenAiLlmOptions
                {
                    ApiKey = "e2e-openai-key",
                    BaseUrl = new Uri($"{harness.LlmMock.Url}/v1"),
                    ModelName = "e2e-openai-model",
                    UseJsonMode = true,
                });
            });
        }
    }
}
