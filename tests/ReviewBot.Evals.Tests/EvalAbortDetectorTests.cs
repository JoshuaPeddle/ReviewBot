using FluentAssertions;

namespace ReviewBot.Evals.Tests;

public sealed class EvalAbortDetectorTests
{
    [Fact]
    public void DetectsThePlaceholderTheRunnerWrites()
    {
        // Verbatim shape of a placeholder observed in a baseline run: the model spent its
        // whole output allowance reasoning and returned nothing.
        var raw = """
            {"summary": "Eval fixture aborted: LLM error: openai returned an empty response. It consumed 16384 completion tokens against an allowance of 16384 without emitting any content.", "comments": []}
            """;

        EvalAbortDetector.IsAborted(raw).Should().BeTrue();
    }

    [Fact]
    public void DetectsATimeoutPlaceholder()
    {
        var raw = """{"summary": "Eval fixture aborted: timed out after 600s.", "comments": []}""";

        EvalAbortDetector.IsAborted(raw).Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"summary": "No issues found.", "comments": []}""")]
    [InlineData("""{"summary": "The review aborted early handling of the request queue.", "comments": []}""")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void DoesNotFireOnRealResults(string? raw)
    {
        // The second case matters: a genuine review that merely discusses aborting must
        // still be scored. Anchoring on the runner's exact prefix keeps that safe.
        EvalAbortDetector.IsAborted(raw).Should().BeFalse();
    }
}
