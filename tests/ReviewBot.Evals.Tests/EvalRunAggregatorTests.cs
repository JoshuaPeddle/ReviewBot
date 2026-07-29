using FluentAssertions;

namespace ReviewBot.Evals.Tests;

public sealed class EvalRunAggregatorTests
{
    [Fact]
    public void AggregateReportsMeanAndRangeForEachMetric()
    {
        var runs = new[]
        {
            RunScore(precision: 0.9, recall: 1.0, f1: 0.95, [Fixture("001", passed: true)]),
            RunScore(precision: 0.8, recall: 0.8, f1: 0.80, [Fixture("001", passed: true)]),
            RunScore(precision: 0.7, recall: 0.9, f1: 0.80, [Fixture("001", passed: true)])
        };

        var aggregate = new EvalRunAggregator().Aggregate(runs);

        aggregate.Runs.Should().Be(3);
        aggregate.Precision.Mean.Should().BeApproximately(0.8, 0.0001);
        aggregate.Precision.Min.Should().BeApproximately(0.7, 0.0001);
        aggregate.Precision.Max.Should().BeApproximately(0.9, 0.0001);
        aggregate.Precision.Range.Should().BeApproximately(0.2, 0.0001);
        aggregate.F1.Mean.Should().BeApproximately(0.85, 0.0001);
    }

    /// <summary>
    /// The whole point of aggregating: name the fixtures that produce the spread, so a
    /// reader does not mistake a difference smaller than it for a real effect.
    /// </summary>
    [Fact]
    public void AggregateNamesOnlyTheFixturesThatFlipBetweenRuns()
    {
        var runs = new[]
        {
            RunScore(1, 1, 1, [Fixture("always-passes", true), Fixture("flaky", true), Fixture("always-fails", false)]),
            RunScore(1, 1, 1, [Fixture("always-passes", true), Fixture("flaky", false), Fixture("always-fails", false)]),
            RunScore(1, 1, 1, [Fixture("always-passes", true), Fixture("flaky", true), Fixture("always-fails", false)])
        };

        var aggregate = new EvalRunAggregator().Aggregate(runs);

        aggregate.UnstableFixtures.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { FixtureName = "flaky", Passed = 2, Runs = 3 });

        aggregate.FixturePassRates.Should().BeEquivalentTo(
        [
            new { FixtureName = "always-fails", Passed = 0, Runs = 3 },
            new { FixtureName = "flaky", Passed = 2, Runs = 3 },
            new { FixtureName = "always-passes", Passed = 3, Runs = 3 }
        ]);
    }

    [Fact]
    public void AggregateSumsAbortedFixtureRunsAcrossRuns()
    {
        var runs = new[]
        {
            RunScore(1, 1, 1, [Fixture("001", true)], aborted: 1),
            RunScore(1, 1, 1, [Fixture("001", true)], aborted: 2)
        };

        new EvalRunAggregator().Aggregate(runs).AbortedFixtures.Should().Be(3);
    }

    [Fact]
    public void AggregateRejectsAnEmptyRunSet()
    {
        var aggregator = new EvalRunAggregator();

        aggregator.Invoking(a => a.Aggregate([])).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AggregateOfASingleRunReportsNoSpread()
    {
        var aggregate = new EvalRunAggregator()
            .Aggregate([RunScore(0.9, 0.8, 0.85, [Fixture("001", true)])]);

        aggregate.F1.Range.Should().Be(0);
        aggregate.UnstableFixtures.Should().BeEmpty();
    }

    private static EvalRunScore RunScore(
        double precision,
        double recall,
        double f1,
        IReadOnlyList<EvalFixtureScore> fixtures,
        int aborted = 0) =>
        new(
            Passed: fixtures.All(fixture => fixture.Score.Passed),
            TotalFixtures: fixtures.Count,
            PassedFixtures: fixtures.Count(fixture => fixture.Score.Passed),
            FailedFixtures: fixtures.Count(fixture => !fixture.Score.Passed),
            TotalComments: fixtures.Sum(fixture => fixture.Score.TotalComments),
            TruePositives: fixtures.Count(fixture => fixture.Score.Passed),
            FalsePositives: fixtures.Count(fixture => !fixture.Score.Passed),
            FalseNegatives: 0,
            Precision: precision,
            Recall: recall,
            F1: f1,
            Fixtures: fixtures,
            AbortedFixtures: aborted);

    private static EvalFixtureScore Fixture(string key, bool passed) =>
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
                F1: passed ? 1 : 0,
                MustFlagResults: [],
                MustNotFlagResults: [],
                FalsePositiveComments: [],
                AllowedFindingComments: []));
}
