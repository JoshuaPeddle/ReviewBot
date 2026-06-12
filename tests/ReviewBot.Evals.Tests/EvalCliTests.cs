using System.Text.Json;
using FluentAssertions;
using ReviewBot.Evals;

namespace ReviewBot.Evals.Tests;

public sealed class EvalCliTests : IDisposable
{
    private readonly string rootDirectory = Path.Combine(Path.GetTempPath(), $"reviewbot-eval-cli-{Guid.NewGuid():N}");

    [Fact]
    public async Task RunAsyncScoresFixtureSetAndWritesRunJson()
    {
        var fixturesDirectory = Path.Combine(rootDirectory, "fixtures");
        var resultsDirectory = Path.Combine(rootDirectory, "results");
        var outputPath = Path.Combine(rootDirectory, "run.json");
        WriteFixture(fixturesDirectory, "001-null-guard");
        WriteResult(resultsDirectory, "001-null-guard");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await global::EvalCli.RunAsync(
            ["score", "--fixtures", fixturesDirectory, "--results", resultsDirectory, "--out", outputPath],
            output,
            error);

        exitCode.Should().Be(0);
        output.ToString().Should().BeEmpty();
        error.ToString().Should().BeEmpty();
        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        document.RootElement.GetProperty("passed").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("totalFixtures").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("fixtures").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task RunAsyncRejectsMixedSingleAndSetOptions()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await global::EvalCli.RunAsync(
            ["score", "--fixture", "one", "--result", "one.json", "--fixtures", "many", "--results", "many-results"],
            output,
            error);

        exitCode.Should().Be(2);
        error.ToString().Should().Contain("either --fixture/--result or --fixtures/--results");
    }

    [Fact]
    public async Task RunAsyncComparesRunFilesAndWritesComparisonJson()
    {
        var baselinePath = Path.Combine(rootDirectory, "baseline.json");
        var candidatePath = Path.Combine(rootDirectory, "candidate.json");
        var outputPath = Path.Combine(rootDirectory, "comparison.json");
        WriteRun(baselinePath, FixtureScore("001-null-guard", passed: true, f1: 1));
        WriteRun(candidatePath, FixtureScore("001-null-guard", passed: false, f1: 0));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await global::EvalCli.RunAsync(
            ["compare", baselinePath, candidatePath, "--out", outputPath],
            output,
            error);

        exitCode.Should().Be(1);
        error.ToString().Should().BeEmpty();
        output.ToString().Should().Contain("1 regressed");
        output.ToString().Should().Contain("001-null-guard");
        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        document.RootElement.GetProperty("regressedFixtures").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("fixtures")[0].GetProperty("status").GetString().Should().Be("Regressed");
    }

    [Fact]
    public async Task RunAsyncRejectsLiveRunWithoutApiKeyEnvironmentVariable()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await global::EvalCli.RunAsync(
            [
                "run-live",
                "--fixtures", rootDirectory,
                "--results", Path.Combine(rootDirectory, "results"),
                "--base-url", "http://localhost:1234/v1",
                "--model", "qwen/qwen3.5-9b",
                "--api-key-env", "REVIEWBOT_EVAL_TEST_MISSING_KEY"
            ],
            output,
            error);

        exitCode.Should().Be(2);
        error.ToString().Should().Contain("REVIEWBOT_EVAL_TEST_MISSING_KEY");
    }

    [Fact]
    public void EvalDiffParserParsesMultiFilePatch()
    {
        var files = EvalDiffParser.ParseFiles("""
            diff --git a/src/One.cs b/src/One.cs
            index 1111111..2222222 100644
            --- a/src/One.cs
            +++ b/src/One.cs
            @@ -1,1 +1,1 @@
            -old
            +new
            diff --git a/src/Two.cs b/src/Two.cs
            index 3333333..4444444 100644
            --- a/src/Two.cs
            +++ b/src/Two.cs
            @@ -10,1 +10,2 @@
             keep
            +add
            """);

        files.Should().HaveCount(2);
        files[0].Path.Should().Be("src/One.cs");
        files[0].AdditionsCount.Should().Be(1);
        files[0].DeletionsCount.Should().Be(1);
        files[0].CommentableLines.Should().Contain(1);
        files[1].Path.Should().Be("src/Two.cs");
        files[1].CommentableLines.Should().Contain([10, 11]);
    }

    [Fact]
    public void LiveEvalManifestSerializesRetrievalEvidence()
    {
        var manifest = new LiveEvalManifest(
            StartedAtUtc: DateTimeOffset.Parse("2026-05-28T12:00:00Z"),
            FinishedAtUtc: DateTimeOffset.Parse("2026-05-28T12:01:00Z"),
            FixturesDirectory: "fixtures",
            ResultsDirectory: "results",
            BaseUrl: "http://localhost:1234/v1",
            Model: "qwen/qwen3.5-9b",
            RetrievalEnabled: true,
            ConfigPath: ".github/review-bot.yml",
            ContextTokens: 32768,
            IndexCacheDir: "runs/index",
            Fixtures:
            [
                new LiveEvalFixtureManifest(
                    FixtureKey: "001-example",
                    FixtureName: "Example",
                    Category: "cross_chunk_reference",
                    ResultPath: "results/001-example.json",
                    Status: "succeeded",
                    ElapsedSeconds: 12.3,
                    CommentCount: 1,
                    RetrievalSnippetCount: 1,
                    RetrievalSymbolsQueried: 2,
                    RetrievalSnippets:
                    [
                        new LiveEvalRetrievalSnippet(
                            Path: "src/App.cs",
                            StartLine: 7,
                            EndLine: 9,
                            EstimatedTokens: 12,
                            Sha256: "abc123")
                    ],
                    TokenUsage: null)
            ]);

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var document = JsonDocument.Parse(json);
        var fixture = document.RootElement.GetProperty("fixtures")[0];
        fixture.GetProperty("retrievalSnippetCount").GetInt32().Should().Be(1);
        fixture.GetProperty("retrievalSymbolsQueried").GetInt32().Should().Be(2);
        fixture.GetProperty("retrievalSnippets")[0].GetProperty("sha256").GetString().Should().Be("abc123");
    }

    public void Dispose()
    {
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private static void WriteFixture(string fixturesDirectory, string name)
    {
        var fixtureDirectory = Path.Combine(fixturesDirectory, name);
        Directory.CreateDirectory(fixtureDirectory);
        File.WriteAllText(Path.Combine(fixtureDirectory, "fixture.yaml"), """
            name: Null guard
            category: correctness
            difficulty: medium
            description: |
              Test fixture.
            """);
        File.WriteAllText(Path.Combine(fixtureDirectory, "diff.patch"), """
            diff --git a/src/Review.cs b/src/Review.cs
            @@ -10,1 +10,1 @@
            + return value.Length;
            """);
        File.WriteAllText(Path.Combine(fixtureDirectory, "expected.yaml"), """
            must_flag:
              - path: src/Review.cs
                line_range: [10, 10]
                severity_at_least: warning
                topic: null_guard
                must_mention_any: ["null"]
            max_total_comments: 1
            """);
    }

    private static void WriteResult(string resultsDirectory, string name)
    {
        Directory.CreateDirectory(resultsDirectory);
        File.WriteAllText(Path.Combine(resultsDirectory, $"{name}.json"), """
            {
              "summary": "One issue.",
              "comments": [
                {
                  "path": "src/Review.cs",
                  "line": 10,
                  "severity": "warning",
                  "body": "Restore the null guard."
                }
              ]
            }
            """);
    }

    private static void WriteRun(string path, params EvalFixtureScore[] fixtures)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var runScore = new EvalRunScore(
            Passed: fixtures.All(fixture => fixture.Score.Passed),
            TotalFixtures: fixtures.Length,
            PassedFixtures: fixtures.Count(fixture => fixture.Score.Passed),
            FailedFixtures: fixtures.Count(fixture => !fixture.Score.Passed),
            TotalComments: fixtures.Sum(fixture => fixture.Score.TotalComments),
            TruePositives: fixtures.Count(fixture => fixture.Score.Passed),
            FalsePositives: fixtures.Count(fixture => !fixture.Score.Passed),
            FalseNegatives: 0,
            Precision: fixtures.Count(fixture => fixture.Score.Passed) / (double)fixtures.Length,
            Recall: 1,
            F1: fixtures.Average(fixture => fixture.Score.F1),
            Fixtures: fixtures);

        File.WriteAllText(path, JsonSerializer.Serialize(runScore, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static EvalFixtureScore FixtureScore(string key, bool passed, double f1) =>
        new(
            FixtureName: key,
            FixturePath: Path.Combine("/tmp/reviewbot-evals/fixtures", key),
            ResultPath: Path.Combine("/tmp/reviewbot-evals/results", $"{key}.json"),
            Score: new RuleBasedScore(
                Passed: passed,
                TotalComments: 1,
                MaxTotalComments: null,
                TruePositives: passed ? 1 : 0,
                FalsePositives: passed ? 0 : 1,
                FalseNegatives: 0,
                Precision: passed ? 1 : 0,
                Recall: passed ? 1 : 0,
                F1: f1,
                MustFlagResults: [],
                MustNotFlagResults: [],
                FalsePositiveComments: []));
}
