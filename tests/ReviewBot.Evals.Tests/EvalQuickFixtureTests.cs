using FluentAssertions;

namespace ReviewBot.Evals.Tests;

public sealed class EvalQuickFixtureTests
{
    private const int ExpectedQuickFixtureCount = 16;

    [Fact]
    public async Task QuickFixtureCorpusScoresCleanly()
    {
        var repoRoot = FindRepoRoot();
        var fixturesDirectory = Path.Combine(repoRoot, "tests", "ReviewBot.Evals", "Fixtures");
        var resultsDirectory = Path.Combine(repoRoot, "tests", "ReviewBot.Evals", "CannedResults", "quick");

        var runScore = await new EvalRunScorer().ScoreAsync(fixturesDirectory, resultsDirectory);

        runScore.Passed.Should().BeTrue();
        runScore.TotalFixtures.Should().Be(ExpectedQuickFixtureCount);
        runScore.PassedFixtures.Should().Be(ExpectedQuickFixtureCount);
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

        fixtureDirectories.Should().HaveCount(ExpectedQuickFixtureCount);
        fixtureDirectories.Should().OnlyContain(directory =>
            Directory.Exists(Path.Combine(directory, "repo-state")));
    }

    [Fact]
    public void QuickFixturesCoverChunkedReviewRetrievalGaps()
    {
        var repoRoot = FindRepoRoot();
        var fixturesDirectory = Path.Combine(repoRoot, "tests", "ReviewBot.Evals", "Fixtures");
        var loader = new EvalFixtureLoader();

        var categories = Directory
            .EnumerateDirectories(fixturesDirectory)
            .Where(directory => File.Exists(Path.Combine(directory, "fixture.yaml")))
            .Select(directory => loader.Load(directory).Metadata.Category)
            .ToArray();

        categories.Should().Contain("large_pr_chunking");
        categories.Should().Contain("cross_chunk_reference");
        categories.Count(category => category == "cross_chunk_reference").Should().BeGreaterThanOrEqualTo(4);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ReviewBot.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the ReviewBot repository root.");
    }
}
