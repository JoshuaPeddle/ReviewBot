using FluentAssertions;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;
using ReviewBot.Core.Prompting;

namespace ReviewBot.Core.Tests.Llm;

public class EnsembleMergerTests
{
    private static ReviewResult Sample(params InlineComment[] comments) =>
        new("summary", comments);

    private static InlineComment Comment(
        string path,
        int line,
        Severity severity = Severity.Warning,
        Confidence confidence = Confidence.High,
        string body = "finding") =>
        new(path, line, "RIGHT", body, severity, confidence);

    [Fact]
    public void FindingReportedByEnoughSamplesSurvives()
    {
        var merged = EnsembleMerger.Merge(
            [
                Sample(Comment("a.cs", 10)),
                Sample(Comment("a.cs", 10)),
                Sample(Comment("a.cs", 10))
            ],
            minAgreement: 2);

        merged.Result.Comments.Should().ContainSingle().Which.Line.Should().Be(10);
    }

    [Fact]
    public void FindingBelowThresholdIsDropped()
    {
        var merged = EnsembleMerger.Merge(
            [
                Sample(Comment("a.cs", 10)),
                Sample(),
                Sample()
            ],
            minAgreement: 2);

        merged.Result.Comments.Should().BeEmpty();
    }

    [Fact]
    public void ThresholdOfOneKeepsEveryDistinctFinding()
    {
        var merged = EnsembleMerger.Merge(
            [
                Sample(Comment("a.cs", 10)),
                Sample(Comment("b.cs", 20)),
                Sample(Comment("c.cs", 30))
            ],
            minAgreement: 1);

        merged.Result.Comments.Should().HaveCount(3);
    }

    [Fact]
    public void CommentsWithinLineWindowCountAsTheSameFinding()
    {
        // The same defect rarely lands on the same line twice across samples; exact-line
        // matching would score agreement as disagreement.
        var merged = EnsembleMerger.Merge(
            [
                Sample(Comment("a.cs", 10)),
                Sample(Comment("a.cs", 12))
            ],
            minAgreement: 2,
            lineWindow: 3);

        merged.Result.Comments.Should().ContainSingle();
    }

    [Fact]
    public void CommentsBeyondLineWindowStaySeparateFindings()
    {
        var merged = EnsembleMerger.Merge(
            [
                Sample(Comment("a.cs", 10)),
                Sample(Comment("a.cs", 40))
            ],
            minAgreement: 1,
            lineWindow: 3);

        merged.Result.Comments.Should().HaveCount(2);
    }

    [Fact]
    public void RepeatedCommentsFromOneSampleDoNotReachThresholdAlone()
    {
        // Support is counted per sample. Three comments on one line from a single sample is one
        // opinion, not three votes — otherwise a verbose sample manufactures its own consensus.
        var merged = EnsembleMerger.Merge(
            [
                Sample(Comment("a.cs", 10), Comment("a.cs", 11), Comment("a.cs", 12)),
                Sample()
            ],
            minAgreement: 2);

        merged.Result.Comments.Should().BeEmpty();
    }

    [Fact]
    public void DifferentPathsAtTheSameLineAreDifferentFindings()
    {
        var merged = EnsembleMerger.Merge(
            [
                Sample(Comment("a.cs", 10)),
                Sample(Comment("b.cs", 10))
            ],
            minAgreement: 1);

        merged.Result.Comments.Should().HaveCount(2);
    }

    [Fact]
    public void DifferentSidesAtTheSameLineAreDifferentFindings()
    {
        var left = new InlineComment("a.cs", 10, "LEFT", "finding", Severity.Warning);
        var right = new InlineComment("a.cs", 10, "RIGHT", "finding", Severity.Warning);

        var merged = EnsembleMerger.Merge([Sample(left), Sample(right)], minAgreement: 1);

        merged.Result.Comments.Should().HaveCount(2);
    }

    [Fact]
    public void RepresentativeKeepsTheMostSevereMemberOfTheCluster()
    {
        var merged = EnsembleMerger.Merge(
            [
                Sample(Comment("a.cs", 10, Severity.Info, body: "nit")),
                Sample(Comment("a.cs", 10, Severity.Error, body: "real bug"))
            ],
            minAgreement: 2);

        var comment = merged.Result.Comments.Should().ContainSingle().Subject;
        comment.Severity.Should().Be(Severity.Error);
        comment.Body.Should().Be("real bug");
    }

    [Fact]
    public void ThresholdAboveSampleCountIsClampedRatherThanDroppingEverything()
    {
        // A misconfigured threshold should not be indistinguishable from "the model found
        // nothing", which is how an unclamped comparison would present.
        var merged = EnsembleMerger.Merge(
            [
                Sample(Comment("a.cs", 10)),
                Sample(Comment("a.cs", 10))
            ],
            minAgreement: 99);

        merged.Result.Comments.Should().ContainSingle();
    }

    [Fact]
    public void SubThresholdFindingsAreReportedRatherThanSilentlyDiscarded()
    {
        // Without this the trace cannot distinguish "the model found nothing" from "consensus
        // rejected everything" — both show zero candidates and zero drops. Dogfooding PR #63
        // produced exactly that: 45k completion tokens spent, an empty trace.
        var merged = EnsembleMerger.Merge(
            [
                Sample(Comment("a.cs", 10, body: "rejected")),
                Sample(Comment("a.cs", 10, body: "rejected")),
                Sample(Comment("b.cs", 50, body: "kept")),
                Sample(Comment("b.cs", 50, body: "kept")),
                Sample(Comment("b.cs", 50, body: "kept"))
            ],
            minAgreement: 3);

        merged.Result.Comments.Should().ContainSingle().Which.Path.Should().Be("b.cs");

        var rejection = merged.BelowThreshold.Should().ContainSingle().Subject;
        rejection.Comment.Path.Should().Be("a.cs");
        rejection.Support.Should().Be(2);
        rejection.Required.Should().Be(3);
    }

    [Fact]
    public void NothingIsReportedBelowThresholdWhenEverySampleAgrees()
    {
        var merged = EnsembleMerger.Merge(
            [Sample(Comment("a.cs", 10)), Sample(Comment("a.cs", 10))],
            minAgreement: 2);

        merged.BelowThreshold.Should().BeEmpty();
    }

    [Fact]
    public void RejectionsAreOrderedBySupportSoTheNearMissesReadFirst()
    {
        var merged = EnsembleMerger.Merge(
            [
                Sample(Comment("a.cs", 10), Comment("b.cs", 50)),
                Sample(Comment("b.cs", 50)),
                Sample(Comment("c.cs", 90)),
                Sample(),
                Sample()
            ],
            minAgreement: 4);

        merged.BelowThreshold.Select(rejection => rejection.Comment.Path)
            .Should().Equal("b.cs", "a.cs", "c.cs");
        merged.BelowThreshold[0].Support.Should().Be(2);
    }

    [Fact]
    public void EmptySampleSetProducesEmptyResult()
    {
        var merged = EnsembleMerger.Merge([], minAgreement: 1);

        merged.Result.Comments.Should().BeEmpty();
        merged.Result.Summary.Should().BeEmpty();
    }

    [Fact]
    public void TokenUsageIsSummedAcrossSamples()
    {
        var first = new ReviewResult("s", []) { TokenUsage = new LlmTokenUsage(100, 50) };
        var second = new ReviewResult("s", []) { TokenUsage = new LlmTokenUsage(200, 70) };

        var merged = EnsembleMerger.Merge([first, second], minAgreement: 1);

        merged.Result.TokenUsage!.PromptTokens.Should().Be(300);
        merged.Result.TokenUsage.CompletionTokens.Should().Be(120);
    }
}

public class EnsembleReviewLlmTests
{
    private sealed class StubLlm(Func<int, ReviewResult> factory) : IReviewLlm
    {
        private int calls;

        public int MaxConcurrentRequests => 4;

        public Task<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken ct) =>
            Task.FromResult(factory(Interlocked.Increment(ref calls) - 1));

        public Task<string> CompleteRawAsync(PromptPayload prompt, CancellationToken ct, string phase = "review") =>
            Task.FromResult(string.Empty);
    }

    private static ReviewRequest Request() =>
        new("title", "body", "base", "head", [], ReviewConfig.Default);

    [Fact]
    public async Task WhenEverySampleFailsTheCauseIsNamedRatherThanJustTheCount()
    {
        // "All 5 ensemble samples failed" with no reason is undiagnosable, and the reason
        // (context overflow vs non-convergence vs transport) is what determines the fix.
        var llm = new StubLlm(_ => throw new InvalidOperationException("model did not converge"));
        var ensemble = new EnsembleReviewLlm(llm, samples: 3, minAgreement: 2);

        var act = async () => await ensemble.ReviewAsync(Request(), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("model did not converge");
        thrown.Which.InnerException.Should().NotBeNull();
    }

    [Fact]
    public async Task ASingleFailedSampleDoesNotFailTheReview()
    {
        var llm = new StubLlm(index => index == 0
            ? throw new InvalidOperationException("transient")
            : new ReviewResult("ok", [new InlineComment("a.cs", 10, "RIGHT", "finding", Severity.Warning)]));
        var ensemble = new EnsembleReviewLlm(llm, samples: 3, minAgreement: 2);

        var result = await ensemble.ReviewAsync(Request(), CancellationToken.None);

        result.Comments.Should().ContainSingle();
    }

    [Fact]
    public async Task TheMergedResultCarriesTheAgreementTallyForTheTrace()
    {
        var llm = new StubLlm(index => new ReviewResult(
            "ok",
            index < 2
                ? [new InlineComment("a.cs", 10, "RIGHT", "agreed", Severity.Warning)]
                : [new InlineComment("z.cs", 99, "RIGHT", "lone", Severity.Warning)]));
        var ensemble = new EnsembleReviewLlm(llm, samples: 3, minAgreement: 2);

        var result = await ensemble.ReviewAsync(Request(), CancellationToken.None);

        result.RawLlmResponse.Should().NotBeNullOrEmpty();
        result.RawLlmResponse.Should().Contain("below_threshold").And.Contain("z.cs");
    }
}
