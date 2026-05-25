namespace ReviewBot.Evals;

public sealed class EvalRunComparer
{
    public EvalRunComparison Compare(EvalRunScore baseline, EvalRunScore candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        var baselineFixtures = baseline.Fixtures.ToDictionary(GetFixtureKey, StringComparer.Ordinal);
        var candidateFixtures = candidate.Fixtures.ToDictionary(GetFixtureKey, StringComparer.Ordinal);
        var keys = baselineFixtures.Keys
            .Union(candidateFixtures.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var fixtures = keys
            .Select(key => CompareFixture(
                key,
                baselineFixtures.GetValueOrDefault(key),
                candidateFixtures.GetValueOrDefault(key)))
            .ToArray();

        return new EvalRunComparison(
            RegressedFixtures: fixtures.Count(fixture => fixture.Status == EvalFixtureComparisonStatus.Regressed),
            ImprovedFixtures: fixtures.Count(fixture => fixture.Status == EvalFixtureComparisonStatus.Improved),
            UnchangedFixtures: fixtures.Count(fixture => fixture.Status == EvalFixtureComparisonStatus.Unchanged),
            AddedFixtures: fixtures.Count(fixture => fixture.Status == EvalFixtureComparisonStatus.Added),
            RemovedFixtures: fixtures.Count(fixture => fixture.Status == EvalFixtureComparisonStatus.Removed),
            BaselinePrecision: baseline.Precision,
            CandidatePrecision: candidate.Precision,
            DeltaPrecision: candidate.Precision - baseline.Precision,
            BaselineRecall: baseline.Recall,
            CandidateRecall: candidate.Recall,
            DeltaRecall: candidate.Recall - baseline.Recall,
            BaselineF1: baseline.F1,
            CandidateF1: candidate.F1,
            DeltaF1: candidate.F1 - baseline.F1,
            Fixtures: fixtures);
    }

    private static EvalFixtureComparison CompareFixture(
        string key,
        EvalFixtureScore? baseline,
        EvalFixtureScore? candidate)
    {
        if (baseline is null)
        {
            return new EvalFixtureComparison(
                key,
                candidate!.FixtureName,
                null,
                candidate.Score.Passed,
                null,
                candidate.Score.F1,
                null,
                EvalFixtureComparisonStatus.Added);
        }

        if (candidate is null)
        {
            return new EvalFixtureComparison(
                key,
                baseline.FixtureName,
                baseline.Score.Passed,
                null,
                baseline.Score.F1,
                null,
                null,
                EvalFixtureComparisonStatus.Removed);
        }

        var deltaF1 = candidate.Score.F1 - baseline.Score.F1;
        var status = GetStatus(baseline.Score.Passed, candidate.Score.Passed, deltaF1);

        return new EvalFixtureComparison(
            key,
            candidate.FixtureName,
            baseline.Score.Passed,
            candidate.Score.Passed,
            baseline.Score.F1,
            candidate.Score.F1,
            deltaF1,
            status);
    }

    private static EvalFixtureComparisonStatus GetStatus(bool baselinePassed, bool candidatePassed, double deltaF1)
    {
        const double epsilon = 0.0001;

        if (baselinePassed && !candidatePassed)
        {
            return EvalFixtureComparisonStatus.Regressed;
        }

        if (!baselinePassed && candidatePassed)
        {
            return EvalFixtureComparisonStatus.Improved;
        }

        if (deltaF1 < -epsilon)
        {
            return EvalFixtureComparisonStatus.Regressed;
        }

        if (deltaF1 > epsilon)
        {
            return EvalFixtureComparisonStatus.Improved;
        }

        return EvalFixtureComparisonStatus.Unchanged;
    }

    private static string GetFixtureKey(EvalFixtureScore fixture)
    {
        var directoryName = Path.GetFileName(fixture.FixturePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(directoryName) ? fixture.FixtureName : directoryName;
    }
}

public sealed record EvalRunComparison(
    int RegressedFixtures,
    int ImprovedFixtures,
    int UnchangedFixtures,
    int AddedFixtures,
    int RemovedFixtures,
    double BaselinePrecision,
    double CandidatePrecision,
    double DeltaPrecision,
    double BaselineRecall,
    double CandidateRecall,
    double DeltaRecall,
    double BaselineF1,
    double CandidateF1,
    double DeltaF1,
    IReadOnlyList<EvalFixtureComparison> Fixtures);

public sealed record EvalFixtureComparison(
    string FixtureKey,
    string FixtureName,
    bool? BaselinePassed,
    bool? CandidatePassed,
    double? BaselineF1,
    double? CandidateF1,
    double? DeltaF1,
    EvalFixtureComparisonStatus Status);

public enum EvalFixtureComparisonStatus
{
    Unchanged,
    Improved,
    Regressed,
    Added,
    Removed
}
