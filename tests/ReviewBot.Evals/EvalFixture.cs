namespace ReviewBot.Evals;

public sealed record EvalFixture(
    string DirectoryPath,
    FixtureMetadata Metadata,
    string DiffPatch,
    ExpectedFindings Expected);

/// <param name="Name">Reporting-only label. Never goes into the model prompt — a name like
/// "Webhook signature validator leaks secret state" is an answer key.</param>
/// <param name="Description">Reporting-only. Same hazard as <paramref name="Name"/>.</param>
/// <param name="PrTitle">Optional neutral PR title to send to the model. When null the runner
/// synthesizes one from the changed file paths.</param>
/// <param name="PrBody">Optional neutral PR body to send to the model. When null the model gets
/// an empty body rather than the fixture's description.</param>
public sealed record FixtureMetadata(
    string Name,
    string Category,
    string Difficulty,
    string Description,
    string? PrTitle = null,
    string? PrBody = null);
