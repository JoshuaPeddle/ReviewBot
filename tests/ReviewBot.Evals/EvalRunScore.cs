namespace ReviewBot.Evals;

public sealed record EvalRunScore(
    bool Passed,
    int TotalFixtures,
    int PassedFixtures,
    int FailedFixtures,
    int TotalComments,
    int TruePositives,
    int FalsePositives,
    int FalseNegatives,
    double Precision,
    double Recall,
    double F1,
    IReadOnlyList<EvalFixtureScore> Fixtures,
    // Fixture runs where the provider never returned a usable answer. Excluded from
    // every rate above: an aborted request is missing data, not a wrong answer, and
    // scoring it as a miss reports an infrastructure failure as a quality result.
    // Reported so a run that lost fixtures cannot look like a clean one.
    int AbortedFixtures = 0);

public sealed record EvalFixtureScore(
    string FixtureName,
    string FixturePath,
    string ResultPath,
    RuleBasedScore Score);
