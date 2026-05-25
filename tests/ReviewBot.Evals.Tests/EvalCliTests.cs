using System.Text.Json;
using FluentAssertions;

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
}
