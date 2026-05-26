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
