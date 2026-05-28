using System.Collections.ObjectModel;

namespace ReviewBot.Core.Domain;

public sealed record ReviewConfig(
    bool Enabled,
    ModelConfig Model,
    ReviewOutputConfig Review,
    IReadOnlyList<string> Ignore,
    IReadOnlyList<string> Focus,
    string Instructions,
    GroundingConfig Grounding,
    RetrievalConfig Retrieval)
{
    public static ReviewConfig Default { get; } = new(
        Enabled: true,
        Model: new ModelConfig(
            Provider: "openai",
            Name: "qwen3.5:9b-q4_K_M",
            BaseUrlEnvVar: null),
        Review: new ReviewOutputConfig(
            InlineComments: true,
            Summary: true,
            MaxFiles: 50,
            MaxPatchLines: 1500,
            Trigger: new TriggerConfig(
                OnReviewRequest: true,
                OnPush: false)),
        Ignore: ReadOnlyCollection<string>.Empty,
        Focus: new ReadOnlyCollection<string>(
        [
            "correctness",
            "security",
            "concurrency",
            "error_handling"
        ]),
        Instructions: string.Empty,
        Grounding: GroundingConfig.Default,
        Retrieval: RetrievalConfig.Default);
}

public sealed record GroundingConfig(
    bool Enabled,
    bool Build,
    bool Tests,
    bool LocalTests,
    int BuildTimeoutSeconds,
    int TestTimeoutSeconds,
    string? BuildCommand,
    string? TestCommand)
{
    public static GroundingConfig Default { get; } = new(
        Enabled: true,
        Build: false,
        Tests: false,
        LocalTests: false,
        BuildTimeoutSeconds: 120,
        TestTimeoutSeconds: 300,
        BuildCommand: null,
        TestCommand: null);
}

public sealed record RetrievalConfig(
    bool Enabled,
    int MaxBytes,
    string SymbolLookupDepth,
    bool Embeddings,
    string IndexCacheDir)
{
    public const string DefinitionsDepth = "definitions";
    public const string CallersDepth = "callers";
    public const string BothDepth = "both";

    public static RetrievalConfig Default { get; } = new(
        Enabled: false,
        MaxBytes: 102_400,
        SymbolLookupDepth: CallersDepth,
        Embeddings: false,
        IndexCacheDir: "/var/cache/reviewbot/index");
}

public sealed record ModelConfig(
    string Provider,
    string Name,
    string? BaseUrlEnvVar);

public sealed record ReviewOutputConfig(
    bool InlineComments,
    bool Summary,
    int MaxFiles,
    int MaxPatchLines,
    TriggerConfig Trigger,
    Confidence MinConfidence = Confidence.Low,
    bool SelfCritique = false,
    bool AgenticContext = false,
    int MaxContextRequests = 5,
    int MaxContextFileBytes = 50_000,
    bool RequestChangesOnError = false,
    bool ApproveIfClean = false,
    int FullFileMaxBytes = 0,
    int ResponseReserveTokens = 4_096,
    bool ChunkedReview = true,
    int MaxChunks = 10,
    double ChunkHeadroom = 0.80);

public sealed record TriggerConfig(
    bool OnReviewRequest,
    bool OnPush);
