using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReviewBot.Api.Tracing;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace ReviewBot.Api.Tests.Tracing;

public class JsonReviewTraceWriterTests : IDisposable
{
    private readonly string tempDir;

    public JsonReviewTraceWriterTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"reviewbot-trace-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
    }

    [Fact]
    public async Task WriteAsync_CreatesJsonFileWithExpectedFields()
    {
        var writer = CreateWriter(enabled: true);
        var trace = CreateTrace();

        await writer.WriteAsync(trace);

        var filePath = Path.Combine(tempDir, "octo-org", "reviewbot", "42-delivery-abc.json");
        File.Exists(filePath).Should().BeTrue();

        var json = await File.ReadAllTextAsync(filePath);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("delivery_id").GetString().Should().Be("delivery-abc");
        root.GetProperty("owner").GetString().Should().Be("octo-org");
        root.GetProperty("repo").GetString().Should().Be("reviewbot");
        root.GetProperty("pr_number").GetInt32().Should().Be(42);
        root.GetProperty("head_sha").GetString().Should().Be("head-sha");
        root.GetProperty("base_sha").GetString().Should().Be("base-sha");
        root.GetProperty("model_provider").GetString().Should().Be("openai");
        root.GetProperty("model_name").GetString().Should().Be("qwen3:9b");
        root.GetProperty("review_type").GetString().Should().Be("first_review");
        root.GetProperty("chunk_count").GetInt32().Should().Be(1);

        var files = root.GetProperty("files_reviewed").EnumerateArray().Select(e => e.GetString()).ToArray();
        files.Should().Equal("src/Foo.cs", "src/Bar.cs");

        var budget = root.GetProperty("prompt_budget");
        budget.GetProperty("model_context_limit_tokens").GetInt32().Should().Be(32768);
        budget.GetProperty("content_budget_tokens").GetInt32().Should().BeGreaterThan(0);

        var candidateComments = root.GetProperty("candidate_comments").EnumerateArray().ToArray();
        candidateComments.Should().HaveCount(2);
        candidateComments[0].GetProperty("severity").GetString().Should().Be("error");
        candidateComments[0].GetProperty("confidence").GetString().Should().Be("high");

        var finalComments = root.GetProperty("final_comments").EnumerateArray().ToArray();
        finalComments.Should().HaveCount(1);
        finalComments[0].GetProperty("path").GetString().Should().Be("src/Foo.cs");
    }

    [Fact]
    public async Task WriteAsync_DoesNothingWhenDisabled()
    {
        var writer = CreateWriter(enabled: false);
        var trace = CreateTrace();

        await writer.WriteAsync(trace);

        var dir = Path.Combine(tempDir, "octo-org", "reviewbot");
        Directory.Exists(dir).Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_WritesAtomicallyViaTempFile()
    {
        var writer = CreateWriter(enabled: true);
        var trace = CreateTrace();

        await writer.WriteAsync(trace);

        var dir = Path.Combine(tempDir, "octo-org", "reviewbot");
        var jsonFiles = Directory.GetFiles(dir, "*.json");
        var tmpFiles = Directory.GetFiles(dir, "*.tmp");
        jsonFiles.Should().HaveCount(1);
        tmpFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task WriteAsync_IncludesTokenUsageWhenPresent()
    {
        var writer = CreateWriter(enabled: true);
        var trace = CreateTrace(tokenUsage: new TraceLlmTokenUsage
        {
            PromptTokens = 1500,
            CompletionTokens = 300,
            CachedPromptTokens = 800
        });

        await writer.WriteAsync(trace);

        var filePath = Path.Combine(tempDir, "octo-org", "reviewbot", "42-delivery-abc.json");
        var json = await File.ReadAllTextAsync(filePath);
        var doc = JsonDocument.Parse(json);
        var usage = doc.RootElement.GetProperty("token_usage");

        usage.GetProperty("prompt_tokens").GetInt32().Should().Be(1500);
        usage.GetProperty("completion_tokens").GetInt32().Should().Be(300);
        usage.GetProperty("cached_prompt_tokens").GetInt32().Should().Be(800);
    }

    [Fact]
    public async Task WriteAsync_IncludesEstimatedCostUsdWhenPresent()
    {
        var writer = CreateWriter(enabled: true);
        var trace = CreateTrace(estimatedCostUsd: 0.042m);

        await writer.WriteAsync(trace);

        var filePath = Path.Combine(tempDir, "octo-org", "reviewbot", "42-delivery-abc.json");
        var json = await File.ReadAllTextAsync(filePath);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("estimated_cost_usd").GetDecimal().Should().Be(0.042m);
    }

    [Fact]
    public async Task WriteAsync_OmitsEstimatedCostUsdWhenNull()
    {
        var writer = CreateWriter(enabled: true);
        var trace = CreateTrace(estimatedCostUsd: null);

        await writer.WriteAsync(trace);

        var filePath = Path.Combine(tempDir, "octo-org", "reviewbot", "42-delivery-abc.json");
        var json = await File.ReadAllTextAsync(filePath);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.TryGetProperty("estimated_cost_usd", out var prop).Should().BeTrue();
        prop.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task WriteAsync_SerializesPromptBudgetSections()
    {
        var writer = CreateWriter(enabled: true);
        var budget = ReviewBot.Core.Context.PromptBudget.Create(32768, 500, 100, 4096);
        budget = budget.ConsumeAvailable("retrieval", 200, out _);
        budget = budget.ConsumeAvailable("diff", 1000, out _);

        var trace = CreateTrace(promptBudget: budget);

        await writer.WriteAsync(trace);

        var filePath = Path.Combine(tempDir, "octo-org", "reviewbot", "42-delivery-abc.json");
        var json = await File.ReadAllTextAsync(filePath);
        var doc = JsonDocument.Parse(json);
        var sections = doc.RootElement
            .GetProperty("prompt_budget")
            .GetProperty("consumed_sections")
            .EnumerateArray()
            .ToArray();

        sections.Should().HaveCount(2);
        sections.Should().Contain(s => s.GetProperty("name").GetString() == "retrieval");
        sections.Should().Contain(s => s.GetProperty("name").GetString() == "diff");
    }

    [Fact]
    public async Task WriteAsync_SerializesChunkTracesWithPromptsWhenIncludePromptsIsTrue()
    {
        var writer = CreateWriter(enabled: true, includePrompts: true);
        var trace = CreateTrace(chunkTraces:
        [
            new TraceChunk
            {
                ChunkIndex = 1,
                TotalChunks = 2,
                Files = ["src/A.cs"],
                ElapsedMs = 1234.5,
                PromptSystemBytes = 100,
                PromptUserBytes = 200,
                PromptSystem = "You are a reviewer.",
                PromptUser = "Review this diff.",
                RawLlmResponseBytes = 50,
                RawLlmResponse = "{\"comments\":[]}"
            }
        ]);

        await writer.WriteAsync(trace);

        var filePath = Path.Combine(tempDir, "octo-org", "reviewbot", "42-delivery-abc.json");
        var json = await File.ReadAllTextAsync(filePath);
        var doc = JsonDocument.Parse(json);
        var chunks = doc.RootElement.GetProperty("chunk_traces").EnumerateArray().ToArray();
        chunks.Should().HaveCount(1);
        chunks[0].GetProperty("chunk_index").GetInt32().Should().Be(1);
        chunks[0].GetProperty("total_chunks").GetInt32().Should().Be(2);
        chunks[0].GetProperty("elapsed_ms").GetDouble().Should().BeApproximately(1234.5, 0.1);
        chunks[0].GetProperty("prompt_system").GetString().Should().Be("You are a reviewer.");
        chunks[0].GetProperty("prompt_user").GetString().Should().Be("Review this diff.");
        chunks[0].GetProperty("raw_llm_response").GetString().Should().Be("{\"comments\":[]}");
        chunks[0].GetProperty("prompt_system_bytes").GetInt32().Should().Be(100);
    }

    [Fact]
    public async Task WriteAsync_OmitsPromptTextWhenIncludePromptsIsFalse()
    {
        var writer = CreateWriter(enabled: true, includePrompts: false);
        var trace = CreateTrace(chunkTraces:
        [
            new TraceChunk
            {
                ChunkIndex = 1,
                TotalChunks = 1,
                Files = ["src/A.cs"],
                ElapsedMs = 500,
                PromptSystemBytes = 100,
                PromptUserBytes = 200,
                PromptSystem = null,
                PromptUser = null,
                RawLlmResponseBytes = 0,
                RawLlmResponse = null
            }
        ]);

        await writer.WriteAsync(trace);

        var filePath = Path.Combine(tempDir, "octo-org", "reviewbot", "42-delivery-abc.json");
        var json = await File.ReadAllTextAsync(filePath);
        var doc = JsonDocument.Parse(json);
        var chunks = doc.RootElement.GetProperty("chunk_traces").EnumerateArray().ToArray();
        chunks.Should().HaveCount(1);
        chunks[0].TryGetProperty("prompt_system", out var ps).Should().BeTrue();
        ps.ValueKind.Should().Be(JsonValueKind.Null);
        chunks[0].GetProperty("prompt_system_bytes").GetInt32().Should().Be(100);
        chunks[0].GetProperty("prompt_user_bytes").GetInt32().Should().Be(200);
    }

    [Fact]
    public async Task WriteAsync_SerializesTimings()
    {
        var writer = CreateWriter(enabled: true);
        var trace = CreateTrace(timings: new TraceTimings
        {
            GroundingMs = 120.5,
            RetrievalMs = 45.0,
            FullFileContextMs = 30.0,
            TotalMs = 3200.0
        });

        await writer.WriteAsync(trace);

        var filePath = Path.Combine(tempDir, "octo-org", "reviewbot", "42-delivery-abc.json");
        var json = await File.ReadAllTextAsync(filePath);
        var doc = JsonDocument.Parse(json);
        var timings = doc.RootElement.GetProperty("timings");
        timings.GetProperty("grounding_ms").GetDouble().Should().BeApproximately(120.5, 0.01);
        timings.GetProperty("retrieval_ms").GetDouble().Should().BeApproximately(45.0, 0.01);
        timings.GetProperty("total_ms").GetDouble().Should().BeApproximately(3200.0, 0.01);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private JsonReviewTraceWriter CreateWriter(bool enabled, bool includePrompts = true) =>
        new(
            MsOptions.Create(new TracingOptions { Enabled = enabled, TracesDir = tempDir, IncludePrompts = includePrompts }),
            NullLogger<JsonReviewTraceWriter>.Instance);

    private static ReviewTrace CreateTrace(
        TraceLlmTokenUsage? tokenUsage = null,
        ReviewBot.Core.Context.PromptBudget? promptBudget = null,
        IReadOnlyList<TraceChunk>? chunkTraces = null,
        TraceTimings? timings = null,
        decimal? estimatedCostUsd = null)
    {
        var budget = promptBudget ?? ReviewBot.Core.Context.PromptBudget.Create(32768, 500, 100, 4096);
        return new ReviewTrace
        {
            DeliveryId = "delivery-abc",
            TimestampUtc = new DateTimeOffset(2026, 5, 27, 10, 0, 0, TimeSpan.Zero),
            Owner = "octo-org",
            Repo = "reviewbot",
            PrNumber = 42,
            HeadSha = "head-sha",
            BaseSha = "base-sha",
            PrTitle = "Improve parser",
            TriggerReason = "review_requested",
            ReviewType = "first_review",
            ModelProvider = "openai",
            ModelName = "qwen3:9b",
            FilesReviewed = ["src/Foo.cs", "src/Bar.cs"],
            ChunkCount = 1,
            RetrievalSnippetsCount = 0,
            PromptBudget = new TraceBudgetSnapshot
            {
                ModelContextLimitTokens = budget.ModelContextLimitTokens,
                SystemPromptTokens = budget.SystemPromptTokens,
                GroundingTokens = budget.GroundingTokens,
                ResponseReserveTokens = budget.ResponseReserveTokens,
                ContentBudgetTokens = budget.ContentBudgetTokens,
                ConsumedContentTokens = budget.ConsumedContentTokens,
                RemainingContentTokens = budget.RemainingContentTokens,
                ConsumedSections = budget.ConsumedSections
                    .Select(s => new TraceBudgetSectionSnapshot { Name = s.Name, Tokens = s.Tokens })
                    .ToArray()
            },
            ResultSummary = "Found one issue.",
            CandidateComments =
            [
                new TraceComment { Path = "src/Foo.cs", Line = 5, Side = "RIGHT", Body = "Null check missing.", Severity = "error", Confidence = "high" },
                new TraceComment { Path = "src/Bar.cs", Line = 10, Side = "RIGHT", Body = "Consider extracting method.", Severity = "info", Confidence = "low" }
            ],
            FinalComments =
            [
                new TraceComment { Path = "src/Foo.cs", Line = 5, Side = "RIGHT", Body = "Null check missing.", Severity = "error", Confidence = "high" }
            ],
            TokenUsage = tokenUsage,
            EstimatedCostUsd = estimatedCostUsd,
            ChunkTraces = chunkTraces,
            Timings = timings
        };
    }
}
