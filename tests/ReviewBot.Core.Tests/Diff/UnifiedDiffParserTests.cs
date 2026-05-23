using FluentAssertions;
using ReviewBot.Core.Diff;

namespace ReviewBot.Core.Tests.Diff;

public class UnifiedDiffParserTests
{
    [Fact]
    public void SingleHunkWithOnlyAdditionsReturnsAllNewLines()
    {
        const string patch = """
            @@ -0,0 +1,3 @@
            +first
            +second
            +third
            """;

        var lines = UnifiedDiffParser.GetCommentableLines(patch);

        lines.Should().BeEquivalentTo([1, 2, 3]);
    }

    [Fact]
    public void SingleHunkWithMixedLinesReturnsOnlyNewSideLines()
    {
        const string patch = """
            @@ -10,4 +20,5 @@ public void Review()
             existing line
            -removed line
            +added line
             another existing line
            -old branch
            +new branch
            +extra branch
            """;

        var lines = UnifiedDiffParser.GetCommentableLines(patch);

        lines.Should().BeEquivalentTo([20, 21, 22, 23, 24]);
    }

    [Fact]
    public void MultipleHunksPreserveGapsBetweenNewLineRanges()
    {
        const string patch = """
            @@ -1,2 +1,3 @@
             using System;
            +using System.Linq;
             namespace ReviewBot;
            @@ -10,2 +20,2 @@ public sealed class Worker
            -    Start();
            +    StartAsync();
                 Stop();
            """;

        var lines = UnifiedDiffParser.GetCommentableLines(patch);

        lines.Should().BeEquivalentTo([1, 2, 3, 20, 21]);
    }

    [Fact]
    public void HunkHeaderWithOmittedCountsDefaultsToSingleLine()
    {
        const string patch = """
            @@ -1 +1 @@
            +replacement
            """;

        var lines = UnifiedDiffParser.GetCommentableLines(patch);

        lines.Should().BeEquivalentTo([1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("diff --git a/file.cs b/file.cs")]
    public void EmptyNullWhitespaceOrPatchWithoutHunksReturnsEmptySet(string? patch)
    {
        var lines = UnifiedDiffParser.GetCommentableLines(patch);

        lines.Should().BeEmpty();
    }

    [Fact]
    public void IgnoresNoNewlineMarkerWithoutAdvancingLineNumbers()
    {
        const string patch = """
            @@ -1,2 +1,2 @@
             existing
            \ No newline at end of file
            +added
            """;

        var lines = UnifiedDiffParser.GetCommentableLines(patch);

        lines.Should().BeEquivalentTo([1, 2]);
    }

    [Fact]
    public void RealisticFixtureReturnsExactCommentableLines()
    {
        const string patch = """
            @@ -3,9 +3,11 @@ public sealed class ReviewWorker
             private readonly IQueue queue;
             private readonly ILogger logger;
            -private readonly bool enabled;
            +private readonly ReviewConfig config;
             public ReviewWorker(IQueue queue, ILogger logger)
             {
                 this.queue = queue;
                 this.logger = logger;
            +    config = ReviewConfig.Default;
             }
            @@ -25,8 +27,10 @@ public Task RunAsync(CancellationToken ct)
             {
            -    if (!enabled)
            +    if (!config.Enabled)
                 {
                     return Task.CompletedTask;
                 }
            +    logger.LogInformation("Review started");
                 return queue.ReadAsync(ct);
             }
            """;

        var lines = UnifiedDiffParser.GetCommentableLines(patch);

        lines.Should().BeEquivalentTo([3, 4, 5, 6, 7, 8, 9, 10, 11, 27, 28, 29, 30, 31, 32, 33, 34]);
    }

    [Fact]
    public void MalformedHunkHeaderThrowsFormatException()
    {
        const string patch = """
            @@ not a hunk header @@
            +added
            """;

        var act = () => UnifiedDiffParser.GetCommentableLines(patch);

        act.Should().Throw<FormatException>()
            .WithMessage("Malformed unified diff hunk header:*");
    }
}
