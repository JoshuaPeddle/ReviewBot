using ReviewBot.Core.Domain;

namespace ReviewBot.Evals;

public sealed record RuleBasedScore(
    bool Passed,
    int TotalComments,
    int? MaxTotalComments,
    int TruePositives,
    int FalsePositives,
    int FalseNegatives,
    double Precision,
    double Recall,
    double F1,
    IReadOnlyList<MustFlagScore> MustFlagResults,
    IReadOnlyList<MustNotFlagScore> MustNotFlagResults,
    IReadOnlyList<InlineComment> FalsePositiveComments,
    // Correct-but-uncredited findings, excluded from precision. Recorded so a reader can
    // audit what the corpus waved through rather than taking the number on trust.
    IReadOnlyList<InlineComment> AllowedFindingComments);

public sealed record MustFlagScore(
    string Path,
    int StartLine,
    int EndLine,
    string Topic,
    bool Passed,
    string? MatchedCommentBody,
    string? FailureReason);

public sealed record MustNotFlagScore(
    string Path,
    string Reason,
    Severity SeverityAbove,
    bool Passed,
    IReadOnlyList<InlineComment> ViolatingComments);
