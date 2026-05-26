using FluentAssertions;

namespace ReviewBot.Evals.Tests;

public sealed class EvalQuickFixtureTests
{
    [Fact]
    public async Task QuickFixtureCorpusScoresCleanly()
    {
        var repoRoot = FindRepoRoot();
        var fixturesDirectory = Path.Combine(repoRoot, "tests", "ReviewBot.Evals", "Fixtures");
        var resultsDirectory = Path.Combine(repoRoot, "tests", "ReviewBot.Evals", "CannedResults", "quick");

        var runScore = await new EvalRunScorer().ScoreAsync(fixturesDirectory, resultsDirectory);

        runScore.Passed.Should().BeTrue();
        runScore.TotalFixtures.Should().Be(3);
        runScore.PassedFixtures.Should().Be(3);
        runScore.Precision.Should().Be(1);
        runScore.Recall.Should().Be(1);
        runScore.F1.Should().Be(1);
    }

    [Fact]
    public void QuickFixturesIncludeRepoStateDirectories()
    {
        var repoRoot = FindRepoRoot();
        var fixturesDirectory = Path.Combine(repoRoot, "tests", "ReviewBot.Evals", "Fixtures");

        var fixtureDirectories = Directory
            .EnumerateDirectories(fixturesDirectory)
            .Where(directory => File.Exists(Path.Combine(directory, "fixture.yaml")))
            .ToArray();

        fixtureDirectories.Should().HaveCount(3);
        fixtureDirectories.Should().OnlyContain(directory =>
            Directory.Exists(Path.Combine(directory, "repo-state")));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "development-plan.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the ReviewBot repository root.");
    }
}
