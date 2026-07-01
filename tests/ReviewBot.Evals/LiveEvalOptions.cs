namespace ReviewBot.Evals;

public sealed record LiveEvalOptions(
    string FixturesDirectory,
    string ResultsDirectory,
    string ManifestPath,
    Uri BaseUrl,
    string Model,
    string ApiKey,
    bool RetrievalEnabled,
    string? ConfigPath,
    int ContextTokens,
    string IndexCacheDir,
    int PerFixtureTimeoutSeconds = 240,
    int RequestTimeoutSeconds = 180,
    int MaxTokens = 4096,
    float Temperature = 0.2f,
    bool SelfCritique = false);
