using System.Collections.ObjectModel;

namespace ReviewBot.Core.Domain;

public sealed record ReviewConfig(
    bool Enabled,
    ModelConfig Model,
    ReviewOutputConfig Review,
    IReadOnlyList<string> Ignore,
    IReadOnlyList<string> Focus,
    string Instructions,
    GroundingConfig Grounding)
{
    public static ReviewConfig Default { get; } = new(
        Enabled: true,
        Model: new ModelConfig(
            Provider: "anthropic",
            Name: "claude-opus-4-7",
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
        Grounding: GroundingConfig.Default);
}

public sealed record GroundingConfig(
    bool Enabled,
    bool Build,
    bool Tests,
    int BuildTimeoutSeconds,
    int TestTimeoutSeconds,
    string? BuildCommand,
    string? TestCommand)
{
    public static GroundingConfig Default { get; } = new(
        Enabled: true,
        Build: false,
        Tests: false,
        BuildTimeoutSeconds: 120,
        TestTimeoutSeconds: 300,
        BuildCommand: null,
        TestCommand: null);
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
    int MaxContextFileBytes = 50_000);

public sealed record TriggerConfig(
    bool OnReviewRequest,
    bool OnPush);
