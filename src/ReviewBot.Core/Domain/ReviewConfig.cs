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
            // The host owns model deployment. Repository policy may pin a model,
            // otherwise the worker resolves the configured provider model.
            Name: string.Empty),
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
    string IndexCacheDir,
    int MaxHops = 2)
{
    public const string DefinitionsDepth = "definitions";
    public const string CallersDepth = "callers";
    public const string BothDepth = "both";

    public static RetrievalConfig Default { get; } = new(
        Enabled: true,
        MaxBytes: 102_400,
        // "both" surfaces method/type definitions with their bodies AND top-K
        // caller spans. Definitions are what give the model semantic context
        // for cross-file reasoning; "callers" alone returns only one-line
        // usage rows and silently bypasses body extraction.
        SymbolLookupDepth: BothDepth,
        IndexCacheDir: "cache/index",
        // How many times to follow symbol references outward from the diff. 1 fetches the
        // definitions of symbols the diff names. 2 additionally fetches what those bodies
        // themselves refer to, which is where an invariant lives when the direct callee
        // only delegates — a case the one-hop default cannot see at all.
        MaxHops: 2);
}

public sealed record ModelConfig(
    string Provider,
    string Name);

public sealed record ReviewOutputConfig(
    bool InlineComments,
    bool Summary,
    int MaxFiles,
    int MaxPatchLines,
    TriggerConfig Trigger,
    Confidence MinConfidence = Confidence.Medium,
    bool SelfCritique = true,
    bool AgenticContext = false,
    int MaxContextRequests = 5,
    int MaxContextFileBytes = 50_000,
    bool RequestChangesOnError = false,
    bool ApproveIfClean = false,
    int FullFileMaxBytes = 65_536,
    int ResponseReserveTokens = 4_096,
    int MaxChunks = 10,
    double ChunkHeadroom = 0.80)
{
    // Verify findings against ground truth (build diagnostics) before posting.
    // A no-op unless build grounding produces diagnostics; only upgrades findings.
    public VerificationConfig Verification { get; init; } = new();

    // Self-consistency: review the same diff several times and keep what the samples agree on.
    public EnsembleConfig Ensemble { get; init; } = new();
}

public sealed record VerificationConfig(bool Enabled = true);

/// <summary>
/// Self-consistency sampling. <see cref="Samples"/> of 1 disables it and is the default,
/// because the mechanism costs <c>Samples</c>× the tokens of a single review.
/// </summary>
/// <param name="Samples">How many independent reviews of the same diff to run.</param>
/// <param name="MinAgreement">
/// How many distinct samples must report a finding for it to survive. Measured on the
/// 27-fixture corpus at <c>Samples=5</c>: 3 is the optimum (P 0.984 / R 0.955 / F1 0.969
/// against 0.899 / 0.879 / 0.887 for a single sample). Lower trades precision for recall,
/// <c>Samples</c> itself requires unanimity and costs recall sharply.
/// </param>
/// <param name="LineWindow">
/// How far apart two comments can sit and still count as the same finding. Samples disagree
/// about the exact line of a defect, so exact matching would read agreement as disagreement.
/// </param>
public sealed record EnsembleConfig(
    int Samples = 1,
    int MinAgreement = 3,
    int LineWindow = 3);

public sealed record TriggerConfig(
    bool OnReviewRequest,
    bool OnPush);
