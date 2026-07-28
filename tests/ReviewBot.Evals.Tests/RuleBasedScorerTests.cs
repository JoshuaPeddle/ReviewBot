using FluentAssertions;
using ReviewBot.Core.Domain;

namespace ReviewBot.Evals.Tests;

public class RuleBasedScorerTests
{
    [Fact]
    public void ScorePassesWhenMustFlagMatchesAndMustNotStaysBelowCeiling()
    {
        var fixture = CreateFixture(
            mustFlag:
            [
                new MustFlagExpectation(
                    "src/Review.cs",
                    StartLine: 20,
                    EndLine: 22,
                    Severity.Warning,
                    "null_guard",
                    ["null", "guard"])
            ],
            mustNotFlag:
            [
                new MustNotFlagExpectation(
                    "src/Generated.cs",
                    "Generated file comments are noisy.",
                    Severity.Info)
            ],
            maxTotalComments: 2);
        var result = new ReviewResult(
            "One real issue.",
            [
                new InlineComment("src/Review.cs", 21, "RIGHT", "Add the null guard back before dereferencing.", Severity.Warning),
                new InlineComment("src/Generated.cs", 3, "RIGHT", "Tiny style note.", Severity.Info)
            ]);

        var score = new RuleBasedScorer().Score(fixture, result);

        score.Passed.Should().BeTrue();
        score.TruePositives.Should().Be(1);
        score.FalsePositives.Should().Be(0);
        score.FalseNegatives.Should().Be(0);
        score.Precision.Should().Be(1);
        score.Recall.Should().Be(1);
        score.F1.Should().Be(1);
    }

    [Fact]
    public void ScoreFailsWhenKeywordIsMissing()
    {
        var fixture = CreateFixture(
            mustFlag:
            [
                new MustFlagExpectation(
                    "src/Review.cs",
                    StartLine: 20,
                    EndLine: 22,
                    Severity.Warning,
                    "null_guard",
                    ["null"])
            ]);
        var result = new ReviewResult(
            "One issue.",
            [new InlineComment("src/Review.cs", 21, "RIGHT", "This branch returns the wrong value.", Severity.Warning)]);

        var score = new RuleBasedScorer().Score(fixture, result);

        score.Passed.Should().BeFalse();
        score.TruePositives.Should().Be(0);
        score.FalseNegatives.Should().Be(1);
        score.MustFlagResults.Should().ContainSingle().Which.FailureReason.Should().Contain("No comment matched");
    }

    [Fact]
    public void ScoreTreatsUnmatchedCommentsAsFalsePositives()
    {
        var fixture = CreateFixture(
            mustFlag:
            [
                new MustFlagExpectation(
                    "src/Review.cs",
                    StartLine: 20,
                    EndLine: 20,
                    Severity.Warning,
                    "null_guard",
                    ["null"])
            ]);
        var result = new ReviewResult(
            "Two comments.",
            [
                new InlineComment("src/Review.cs", 20, "RIGHT", "Restore the null check.", Severity.Warning),
                new InlineComment("src/Other.cs", 5, "RIGHT", "This unrelated warning is not expected.", Severity.Warning)
            ]);

        var score = new RuleBasedScorer().Score(fixture, result);

        score.Passed.Should().BeFalse();
        score.TruePositives.Should().Be(1);
        score.FalsePositives.Should().Be(1);
        score.Precision.Should().Be(0.5);
        score.FalsePositiveComments.Should().ContainSingle()
            .Which.Path.Should().Be("src/Other.cs");
    }

    [Fact]
    public void ScoreFailsWhenMustNotFlagExceedsSeverityCeiling()
    {
        var fixture = CreateFixture(
            mustNotFlag:
            [
                new MustNotFlagExpectation(
                    "src/Channel.cs",
                    "Debatable choice, not a correctness issue.",
                    Severity.Warning)
            ]);
        var result = new ReviewResult(
            "One comment.",
            [new InlineComment("src/Channel.cs", 8, "RIGHT", "This cannot be merged.", Severity.Error)]);

        var score = new RuleBasedScorer().Score(fixture, result);

        score.Passed.Should().BeFalse();
        score.FalsePositives.Should().Be(1);
        score.MustNotFlagResults.Should().ContainSingle().Which.ViolatingComments.Should().ContainSingle();
    }

    [Fact]
    public void ScoreFailsWhenCommentCountExceedsMaximum()
    {
        var fixture = CreateFixture(maxTotalComments: 0);
        var result = new ReviewResult(
            "Noisy.",
            [new InlineComment("src/Review.cs", 1, "RIGHT", "Unexpected.", Severity.Info)]);

        var score = new RuleBasedScorer().Score(fixture, result);

        score.Passed.Should().BeFalse();
        score.TotalComments.Should().Be(1);
        score.MaxTotalComments.Should().Be(0);
    }

    [Fact]
    public void AllowedFindingIsNeitherCreditedNorPenalised()
    {
        var fixture = CreateFixture(
            mayFlag:
            [
                new MayFlagExpectation(
                    "src/Review.cs",
                    StartLine: null,
                    EndLine: null,
                    "mutable_public_array",
                    "Correct C#, just not the bug under test.",
                    ["ImmutableArray"])
            ]);
        var result = new ReviewResult(
            "Reviewed.",
            [new InlineComment("src/Review.cs", 4, "RIGHT", "Prefer ImmutableArray here.", Severity.Warning)]);

        var score = new RuleBasedScorer().Score(fixture, result);

        score.FalsePositives.Should().Be(0);
        score.TruePositives.Should().Be(0);
        score.AllowedFindingComments.Should().ContainSingle();
        score.Passed.Should().BeTrue();
    }

    [Fact]
    public void AllowedFindingDoesNotCountAgainstMaxTotalComments()
    {
        var fixture = CreateFixture(
            maxTotalComments: 0,
            mayFlag:
            [
                new MayFlagExpectation(
                    "src/Review.cs", null, null, "topic", "Sanctioned.", ["ImmutableArray"])
            ]);
        var result = new ReviewResult(
            "Reviewed.",
            [new InlineComment("src/Review.cs", 4, "RIGHT", "Prefer ImmutableArray here.", Severity.Warning)]);

        var score = new RuleBasedScorer().Score(fixture, result);

        // A sanctioned finding is not noise, so the zero-comment cap still holds.
        score.Passed.Should().BeTrue();
        score.TotalComments.Should().Be(1);
    }

    [Fact]
    public void AllowedFindingDoesNotExcuseAnUnrelatedFalsePositive()
    {
        // The mechanism must not become a blanket amnesty for the file it names.
        var fixture = CreateFixture(
            mayFlag:
            [
                new MayFlagExpectation(
                    "src/Review.cs", null, null, "topic", "Sanctioned.", ["ImmutableArray"])
            ]);
        var result = new ReviewResult(
            "Reviewed.",
            [
                new InlineComment("src/Review.cs", 4, "RIGHT", "Prefer ImmutableArray here.", Severity.Warning),
                new InlineComment("src/Review.cs", 9, "RIGHT", "This will not compile.", Severity.Error)
            ]);

        var score = new RuleBasedScorer().Score(fixture, result);

        score.AllowedFindingComments.Should().ContainSingle();
        score.FalsePositives.Should().Be(1);
        score.FalsePositiveComments.Should().ContainSingle()
            .Which.Body.Should().Be("This will not compile.");
        score.Passed.Should().BeFalse();
    }

    [Fact]
    public void AllowedFindingClearsAMustNotFlagOnTheSamePath()
    {
        // must_not_flag bans a specific wrong claim about a file, not every true
        // statement about it.
        var fixture = CreateFixture(
            mustNotFlag:
            [
                new MustNotFlagExpectation("src/Review.cs", "No syntax hallucinations.", Severity.Info)
            ],
            mayFlag:
            [
                new MayFlagExpectation(
                    "src/Review.cs", null, null, "topic", "Sanctioned.", ["ImmutableArray"])
            ]);
        var result = new ReviewResult(
            "Reviewed.",
            [new InlineComment("src/Review.cs", 4, "RIGHT", "Prefer ImmutableArray here.", Severity.Warning)]);

        var score = new RuleBasedScorer().Score(fixture, result);

        score.MustNotFlagResults.Should().ContainSingle().Which.Passed.Should().BeTrue();
        score.Passed.Should().BeTrue();
    }

    private static EvalFixture CreateFixture(
        IReadOnlyList<MustFlagExpectation>? mustFlag = null,
        IReadOnlyList<MustNotFlagExpectation>? mustNotFlag = null,
        int? maxTotalComments = null,
        IReadOnlyList<MayFlagExpectation>? mayFlag = null)
    {
        return new EvalFixture(
            DirectoryPath: "/tmp/reviewbot-fixture",
            Metadata: new FixtureMetadata(
                "Fixture",
                "correctness",
                "medium",
                "Test fixture."),
            DiffPatch: "diff --git a/file b/file",
            Expected: new ExpectedFindings(
                mustFlag ?? [],
                mustNotFlag ?? [],
                maxTotalComments,
                ExpectedReviewState: null,
                mayFlag ?? []));
    }
}
