namespace ReviewBot.Evals;

public sealed record EvalFixture(
    string DirectoryPath,
    FixtureMetadata Metadata,
    string DiffPatch,
    ExpectedFindings Expected);

public sealed record FixtureMetadata(
    string Name,
    string Category,
    string Difficulty,
    string Description);
