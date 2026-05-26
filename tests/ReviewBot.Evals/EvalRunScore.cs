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
    IReadOnlyList<EvalFixtureScore> Fixtures);

public sealed record EvalFixtureScore(
    string FixtureName,
    string FixturePath,
    string ResultPath,
    RuleBasedScore Score);
