using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ReviewBot.Api;
using ReviewBot.Api.Cost;
using ReviewBot.Api.Options;
using ReviewBot.Api.Tracing;
using ReviewBot.Api.Workers;
using ReviewBot.Api.Webhooks;
using ReviewBot.Core.Context;
using ReviewBot.Core.Jobs;
using ReviewBot.Core.Llm;
using ReviewBot.Core.Otel;
using ReviewBot.GitHub.Auth;
using ReviewBot.GitHub.Checks;
using ReviewBot.GitHub.Config;
using ReviewBot.GitHub.Pulls;
using ReviewBot.Grounding;
using ReviewBot.Grounding.Languages.DotNet;
using ReviewBot.Grounding.Languages.Python;
using ReviewBot.Llm.Anthropic;
using ReviewBot.Llm.OpenAi;
using ReviewBot.Persistence;
using ReviewBot.Retrieval;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables(prefix: "REVIEWBOT__");
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>(optional: true);
}

builder.Services.AddOpenApi();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);

builder.Services.AddSingleton<IValidateOptions<WebhookOptions>, WebhookOptionsValidator>();
builder.Services.AddOptions<WebhookOptions>()
    .Bind(builder.Configuration.GetSection(WebhookOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<GitHubAppOptions>, GitHubAppOptionsValidator>();
builder.Services.AddOptions<GitHubAppOptions>()
    .Bind(builder.Configuration.GetSection(GitHubAppOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<PersistenceOptions>, PersistenceOptionsValidator>();
builder.Services.AddOptions<PersistenceOptions>()
    .Bind(builder.Configuration.GetSection(PersistenceOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<WorkerOptions>, WorkerOptionsValidator>();
builder.Services.AddOptions<WorkerOptions>()
    .Bind(builder.Configuration.GetSection(WorkerOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddReviewLlmFactory();
builder.Services.AddPromptBudgeting(options =>
    builder.Configuration.GetSection(ModelContextOptions.SectionName).Bind(options));
builder.Services.AddRetrieval();
builder.Services.AddAnthropicReviewLlm(options =>
    builder.Configuration.GetSection(AnthropicLlmOptions.SectionName).Bind(options));
builder.Services.AddOpenAiReviewLlm(options =>
    builder.Configuration.GetSection(OpenAiLlmOptions.SectionName).Bind(options));

builder.Services.AddSingleton(provider =>
    new GitHubAppJwtSigner(provider.GetRequiredService<IOptions<GitHubAppOptions>>().Value));
builder.Services.AddHttpClient<InstallationTokenClient>()
    .AddReviewBotHttpResilience();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IInstallationTokenProvider>(provider =>
    new CachingInstallationTokenProvider(
        provider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
        provider.GetRequiredService<InstallationTokenClient>(),
        provider.GetRequiredService<TimeProvider>(),
        provider.GetRequiredService<ILogger<CachingInstallationTokenProvider>>()));
builder.Services.AddSingleton<IGitHubClientFactory, OctokitGitHubClientFactory>();
builder.Services.AddSingleton<ICheckRunFetcher, CheckRunFetcher>();
builder.Services.AddSingleton<PullRequestFetcher>();
builder.Services.AddSingleton<IPullRequestFetcher>(provider => provider.GetRequiredService<PullRequestFetcher>());
builder.Services.AddSingleton<ReviewPoster>();
builder.Services.AddSingleton<IReviewPoster>(provider => provider.GetRequiredService<ReviewPoster>());
builder.Services.AddSingleton<RepoConfigFetcher>();
builder.Services.AddSingleton<IRepoConfigFetcher>(provider => provider.GetRequiredService<RepoConfigFetcher>());

var persistenceOptions = builder.Configuration
    .GetSection(PersistenceOptions.SectionName)
    .Get<PersistenceOptions>() ?? new PersistenceOptions();
builder.Services.AddReviewBotPersistence(options => options.UseSqlite(persistenceOptions.ConnectionString));
builder.Services.AddChannelReviewJobQueue();
builder.Services.AddGrounding()
    .AddLanguageDetector<DotNetLanguageDetector>()
    .AddLanguageDetector<PythonLanguageDetector>()
    .AddBuildRunner<DotNetBuildRunner>()
    .AddBuildRunner<PythonBuildRunner>()
    .AddTestRunner<DotNetTestRunner>()
    .AddTestRunner<PythonTestRunner>()
    .AddDiagnosticProvider<RuffDiagnosticProvider>();
builder.Services.AddReviewTracing();
builder.Services.AddOptions<CostRateOptions>()
    .BindConfiguration(CostRateOptions.SectionName);
builder.Services.AddSingleton<IReviewCostCalculator, ReviewCostCalculator>();
builder.Services.AddSingleton<ReviewBotMetrics>();
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource(ReviewBotActivitySource.SourceName)
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddMeter(ReviewBotMetrics.MeterName)
        .AddPrometheusExporter());
builder.Services.AddHostedService<ReviewWorker>();
builder.Services.AddHostedService<DeliveryStoreCleanupService>();
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy())
    .AddDbContextCheck<ReviewBotDbContext>("db");

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ReviewBotDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapWebhookEndpoint();
app.MapPrometheusScrapingEndpoint();
app.MapHealthChecks("/healthz", new HealthCheckOptions());
app.MapGet("/", () =>
{
    var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev";
    return Results.Text($"ReviewBot v{version}", "text/plain");
});

app.Run();

public partial class Program;
