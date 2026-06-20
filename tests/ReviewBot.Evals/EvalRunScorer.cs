using ReviewBot.Core.Llm;

namespace ReviewBot.Evals;

public sealed class EvalRunScorer
{
    private readonly EvalFixtureLoader fixtureLoader;
    private readonly RuleBasedScorer scorer;

    public EvalRunScorer()
        : this(new EvalFixtureLoader(), new RuleBasedScorer())
    {
    }

    public EvalRunScorer(EvalFixtureLoader fixtureLoader, RuleBasedScorer scorer)
    {
        this.fixtureLoader = fixtureLoader ?? throw new ArgumentNullException(nameof(fixtureLoader));
        this.scorer = scorer ?? throw new ArgumentNullException(nameof(scorer));
    }

    public async Task<EvalRunScore> ScoreAsync(string fixturesDirectory, string resultsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixturesDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultsDirectory);

        if (!Directory.Exists(fixturesDirectory))
        {
            throw new DirectoryNotFoundException($"Eval fixtures directory '{fixturesDirectory}' does not exist.");
        }

        if (!Directory.Exists(resultsDirectory))
        {
            throw new DirectoryNotFoundException($"Eval results directory '{resultsDirectory}' does not exist.");
        }

        var fixtureDirectories = Directory
            .EnumerateDirectories(fixturesDirectory)
            .Where(directory => File.Exists(Path.Combine(directory, "fixture.yaml")))
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (fixtureDirectories.Length == 0)
        {
            throw new InvalidDataException($"Eval fixtures directory '{fixturesDirectory}' does not contain any fixture subdirectories.");
        }

        var fixtureScores = new List<EvalFixtureScore>();
        foreach (var fixtureDirectory in fixtureDirectories)
        {
            var fixtureName = Path.GetFileName(fixtureDirectory);
            var resultPath = Path.Combine(resultsDirectory, $"{fixtureName}.json");
            var fixture = fixtureLoader.Load(fixtureDirectory);

            // A missing result file means the fixture was added after the run
            // produced its results, or the runner exited before reaching it.
            // Score it as a failed fixture (empty review) instead of aborting
            // the entire aggregate so partial data is still useful.
            if (!File.Exists(resultPath))
            {
                var emptyResult = new ReviewBot.Core.Domain.ReviewResult(
                    Summary: $"Eval result file missing for fixture '{fixtureName}'.",
                    Comments: Array.Empty<ReviewBot.Core.Domain.InlineComment>(),
                    ContextRequests: Array.Empty<ReviewBot.Core.Domain.ContextRequest>());
                fixtureScores.Add(new EvalFixtureScore(
                    fixture.Metadata.Name,
                    fixture.DirectoryPath,
                    Path.GetFullPath(resultPath),
                    scorer.Score(fixture, emptyResult)));
                continue;
            }

            var rawResult = await File.ReadAllTextAsync(resultPath).ConfigureAwait(false);
            var parseResult = LlmResultParser.Parse(rawResult);
            if (!parseResult.Success)
            {
                throw new InvalidDataException($"Eval result '{resultPath}' could not be parsed: {parseResult.Error}");
            }

            fixtureScores.Add(new EvalFixtureScore(
                fixture.Metadata.Name,
                fixture.DirectoryPath,
                Path.GetFullPath(resultPath),
                scorer.Score(fixture, parseResult.Value!)));
        }

        return Aggregate(fixtureScores);
    }

    private static EvalRunScore Aggregate(IReadOnlyList<EvalFixtureScore> fixtures)
    {
        var totalComments = fixtures.Sum(fixture => fixture.Score.TotalComments);
        var truePositives = fixtures.Sum(fixture => fixture.Score.TruePositives);
        var falsePositives = fixtures.Sum(fixture => fixture.Score.FalsePositives);
        var falseNegatives = fixtures.Sum(fixture => fixture.Score.FalseNegatives);
        var precision = Divide(truePositives, truePositives + falsePositives);
        var recall = Divide(truePositives, truePositives + falseNegatives);
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        var passedFixtures = fixtures.Count(fixture => fixture.Score.Passed);

        return new EvalRunScore(
            Passed: passedFixtures == fixtures.Count,
            TotalFixtures: fixtures.Count,
            PassedFixtures: passedFixtures,
            FailedFixtures: fixtures.Count - passedFixtures,
            TotalComments: totalComments,
            TruePositives: truePositives,
            FalsePositives: falsePositives,
            FalseNegatives: falseNegatives,
            Precision: precision,
            Recall: recall,
            F1: f1,
            Fixtures: fixtures);
    }

    private static double Divide(int numerator, int denominator)
    {
        return denominator == 0 ? 1 : (double)numerator / denominator;
    }
}
