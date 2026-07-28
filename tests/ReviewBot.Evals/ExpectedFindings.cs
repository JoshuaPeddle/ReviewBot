using ReviewBot.Core.Domain;

namespace ReviewBot.Evals;

public sealed record ExpectedFindings(
    IReadOnlyList<MustFlagExpectation> MustFlag,
    IReadOnlyList<MustNotFlagExpectation> MustNotFlag,
    int? MaxTotalComments,
    string? ExpectedReviewState);

public sealed record MustFlagExpectation(
    string Path,
    int StartLine,
    int EndLine,
    Severity SeverityAtLeast,
    string Topic,
    IReadOnlyList<string> MustMentionAny,
    IReadOnlyList<AllowedLocation>? AdditionalLocations = null,
    bool MustBeVerified = false);

public sealed record AllowedLocation(string Path, int StartLine, int EndLine);

public sealed record MustNotFlagExpectation(
    string Path,
    string Reason,
    Severity SeverityAbove);
