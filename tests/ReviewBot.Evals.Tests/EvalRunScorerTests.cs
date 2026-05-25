using FluentAssertions;

namespace ReviewBot.Evals.Tests;

public sealed class EvalRunScorerTests : IDisposable
{
    private readonly string rootDirectory = Path.Combine(Path.GetTempPath(), $"reviewbot-eval-run-{Guid.NewGuid():N}");
    private string FixturesDirectory => Path.Combine(rootDirectory, "fixtures");
    private string ResultsDirectory => Path.Combine(rootDirectory, "results");

    [Fact]
    public async Task ScoreAsyncAggregatesNamedFixtureResultPairs()
    {
        WriteFixture(
            "001-null-guard",
            """
            must_flag:
              - path: src/Review.cs
                line_range: [10, 10]
                severity_at_least: warning
                topic: null_guard
                must_mention_any: ["null"]
            max_total_comments: 1
            """);
        WriteResult(
            "001-null-guard",
            """
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
        WriteFixture(
            "002-clean",
            """
            max_total_comments: 0
            """);
        WriteResult(
            "002-clean",
            """
            {
              "summary": "Unexpected noise.",
              "comments": [
                {
                  "path": "src/Noise.cs",
                  "line": 1,
                  "body": "This is not expected."
                }
              ]
            }
            """);

        var runScore = await new EvalRunScorer().ScoreAsync(FixturesDirectory, ResultsDirectory);

        runScore.Passed.Should().BeFalse();
        runScore.TotalFixtures.Should().Be(2);
        runScore.PassedFixtures.Should().Be(1);
        runScore.FailedFixtures.Should().Be(1);
        runScore.TotalComments.Should().Be(2);
        runScore.TruePositives.Should().Be(1);
        runScore.FalsePositives.Should().Be(1);
        runScore.FalseNegatives.Should().Be(0);
        runScore.Precision.Should().Be(0.5);
        runScore.Recall.Should().Be(1);
        runScore.F1.Should().BeApproximately(2d / 3d, 0.0001);
        runScore.Fixtures.Select(fixture => fixture.ResultPath)
            .Should().Equal(
                Path.GetFullPath(Path.Combine(ResultsDirectory, "001-null-guard.json")),
                Path.GetFullPath(Path.Combine(ResultsDirectory, "002-clean.json")));
    }

    [Fact]
    public async Task ScoreAsyncRejectsFixtureDirectoryWithNoFixtures()
    {
        Directory.CreateDirectory(FixturesDirectory);
        Directory.CreateDirectory(ResultsDirectory);

        var act = () => new EvalRunScorer().ScoreAsync(FixturesDirectory, ResultsDirectory);

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*does not contain any fixture subdirectories*");
    }

    public void Dispose()
    {
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private void WriteFixture(string name, string expectedYaml)
    {
        var fixtureDirectory = Path.Combine(FixturesDirectory, name);
        Directory.CreateDirectory(fixtureDirectory);
        File.WriteAllText(Path.Combine(fixtureDirectory, "fixture.yaml"), $$"""
            name: {{name}}
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
        File.WriteAllText(Path.Combine(fixtureDirectory, "expected.yaml"), expectedYaml);
    }

    private void WriteResult(string name, string resultJson)
    {
        Directory.CreateDirectory(ResultsDirectory);
        File.WriteAllText(Path.Combine(ResultsDirectory, $"{name}.json"), resultJson);
    }
}
