namespace ReviewBot.Evals;

/// <summary>
/// Several scored runs of the same arm, reduced to what a single run cannot tell you:
/// the centre of each metric and how far it moves between runs.
/// </summary>
/// <remarks>
/// Measured on the reference corpus: 8 of 27 fixtures flip between runs at identical
/// code and configuration, so one run is worth roughly ±2 fixtures / ±0.05 F1. Every
/// A/B in this repo was n=1 per arm until this existed, which is how a 0.933 and an
/// 0.884 from the same commit came to look like a regression.
/// </remarks>
public sealed record EvalRunAggregate(
    int Runs,
    MetricSpread Precision,
    MetricSpread Recall,
    MetricSpread F1,
    MetricSpread PassedFixtures,
    int TotalFixtures,
    int AbortedFixtures,
    IReadOnlyList<FixturePassRate> FixturePassRates)
{
    /// <summary>
    /// Fixtures that neither always pass nor always fail. These are the ones that make a
    /// single run unreliable, so they are worth naming rather than averaging away.
    /// </summary>
    public IReadOnlyList<FixturePassRate> UnstableFixtures =>
        this.FixturePassRates.Where(rate => rate.Passed > 0 && rate.Passed < rate.Runs).ToArray();
}

public sealed record MetricSpread(double Mean, double Min, double Max)
{
    public double Range => this.Max - this.Min;

    public static MetricSpread From(IReadOnlyList<double> values) =>
        values.Count == 0
            ? new MetricSpread(0, 0, 0)
            : new MetricSpread(values.Average(), values.Min(), values.Max());
}

public sealed record FixturePassRate(string FixtureName, int Passed, int Runs);

public sealed class EvalRunAggregator
{
    public EvalRunAggregate Aggregate(IReadOnlyList<EvalRunScore> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0)
        {
            throw new ArgumentException("At least one run is required to aggregate.", nameof(runs));
        }

        var passRates = runs
            .SelectMany(run => run.Fixtures)
            .GroupBy(fixture => fixture.FixtureName, StringComparer.Ordinal)
            .Select(group => new FixturePassRate(
                group.Key,
                group.Count(fixture => fixture.Score.Passed),
                group.Count()))
            .OrderBy(rate => rate.Passed)
            .ThenBy(rate => rate.FixtureName, StringComparer.Ordinal)
            .ToArray();

        return new EvalRunAggregate(
            Runs: runs.Count,
            Precision: MetricSpread.From(runs.Select(run => run.Precision).ToArray()),
            Recall: MetricSpread.From(runs.Select(run => run.Recall).ToArray()),
            F1: MetricSpread.From(runs.Select(run => run.F1).ToArray()),
            PassedFixtures: MetricSpread.From(runs.Select(run => (double)run.PassedFixtures).ToArray()),
            TotalFixtures: runs.Max(run => run.TotalFixtures),
            AbortedFixtures: runs.Sum(run => run.AbortedFixtures),
            FixturePassRates: passRates);
    }
}
