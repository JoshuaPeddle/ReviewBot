using ReviewBot.Core.Domain;

namespace ReviewBot.Evals;

public sealed record ExpectedFindings(
    IReadOnlyList<MustFlagExpectation> MustFlag,
    IReadOnlyList<MustNotFlagExpectation> MustNotFlag,
    int? MaxTotalComments,
    string? ExpectedReviewState,
    IReadOnlyList<MayFlagExpectation> MayFlag);

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

/// <summary>
/// A finding that is correct but not the one the fixture is testing for: it earns no
/// credit, and — crucially — costs nothing either.
/// </summary>
/// <remarks>
/// Fixtures target a single bug, so every other comment was scored as a false positive
/// even when it was true. Measured on a live run, 5 of 8 "false positives" were accurate
/// second comments about the bug the fixture already credits, and one (a `readonly int[]`
/// whose elements are still mutable) was simply correct C# the fixture had no slot for.
/// That understates precision by an unknown margin, which in turn puts a ceiling on how
/// well any precision change can be measured. Declaring those findings here removes the
/// distortion without paying the model for them.
/// </remarks>
public sealed record MayFlagExpectation(
    string Path,
    int? StartLine,
    int? EndLine,
    string Topic,
    string Reason,
    IReadOnlyList<string> MustMentionAny);
