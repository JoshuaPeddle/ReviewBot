using FluentAssertions;

namespace ReviewBot.Evals.Tests;

public sealed class EvalRunComparerTests
{
    [Fact]
    public void CompareMarksPassToFailAsRegression()
    {
        var baseline = RunScore([
            Fixture("001-null-guard", passed: true, f1: 1),
            Fixture("002-resource-leak", passed: false, f1: 0.4)
        ]);
        var candidate = RunScore([
            Fixture("001-null-guard", passed: false, f1: 0.5),
            Fixture("002-resource-leak", passed: true, f1: 1)
        ]);

        var comparison = new EvalRunComparer().Compare(baseline, candidate);

        comparison.RegressedFixtures.Should().Be(1);
        comparison.ImprovedFixtures.Should().Be(1);
        comparison.DeltaF1.Should().BeApproximately(0.05, 0.0001);
        comparison.Fixtures.Should().ContainEquivalentOf(new
        {
            FixtureKey = "001-null-guard",
            Status = EvalFixtureComparisonStatus.Regressed,
            BaselinePassed = true,
            CandidatePassed = false
        });
        comparison.Fixtures.Should().ContainEquivalentOf(new
        {
            FixtureKey = "002-resource-leak",
            Status = EvalFixtureComparisonStatus.Improved,
            BaselinePassed = false,
            CandidatePassed = true
        });
    }

    [Fact]
    public void CompareReportsAddedAndRemovedFixtures()
    {
        var baseline = RunScore([
            Fixture("001-old", passed: true, f1: 1)
        ]);
        var candidate = RunScore([
            Fixture("002-new", passed: true, f1: 1)
        ]);

        var comparison = new EvalRunComparer().Compare(baseline, candidate);

        comparison.RemovedFixtures.Should().Be(1);
        comparison.AddedFixtures.Should().Be(1);
        comparison.Fixtures.Select(fixture => fixture.Status)
            .Should().Equal(EvalFixtureComparisonStatus.Removed, EvalFixtureComparisonStatus.Added);
    }

    private static EvalRunScore RunScore(IReadOnlyList<EvalFixtureScore> fixtures)
    {
        var truePositives = fixtures.Count(fixture => fixture.Score.Passed);
        var falsePositives = fixtures.Count(fixture => !fixture.Score.Passed);
        var precision = truePositives + falsePositives == 0 ? 1 : (double)truePositives / (truePositives + falsePositives);
        var recall = truePositives == 0 ? 0 : 1;
        var f1 = fixtures.Count == 0 ? 0 : fixtures.Average(fixture => fixture.Score.F1);

        return new EvalRunScore(
            Passed: fixtures.All(fixture => fixture.Score.Passed),
            TotalFixtures: fixtures.Count,
            PassedFixtures: fixtures.Count(fixture => fixture.Score.Passed),
            FailedFixtures: fixtures.Count(fixture => !fixture.Score.Passed),
            TotalComments: fixtures.Sum(fixture => fixture.Score.TotalComments),
            TruePositives: truePositives,
            FalsePositives: falsePositives,
            FalseNegatives: 0,
            Precision: precision,
            Recall: recall,
            F1: f1,
            Fixtures: fixtures);
    }

    private static EvalFixtureScore Fixture(string key, bool passed, double f1) =>
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
