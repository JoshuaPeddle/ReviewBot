using ReviewBot.Core.Llm;
using ReviewBot.Llm.OpenAi;

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
    bool SelfCritique = false,
    OpenAiSamplingOptions? Sampling = null,
    int Concurrency = 1,
    // Self-consistency. Samples=1 leaves behaviour byte-identical to before these existed.
    // Every sample is persisted alongside the merged result so thresholds can be swept with
    // the `ensemble-rescore` verb instead of re-running the model once per threshold.
    int EnsembleSamples = 1,
    int EnsembleMinAgreement = 1,
    int EnsembleLineWindow = EnsembleMerger.DefaultLineWindow);
