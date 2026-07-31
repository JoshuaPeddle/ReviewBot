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
    /// <summary>
    /// A patch ends with a newline, so splitting produces a trailing empty entry. Treating
    /// that as "not an added line" rejected essentially every added file — the bug
    /// ReviewBot found reviewing its own PR.
    /// </summary>
    [Fact]
    public void TryReconstructAddedFileContentHandlesTheTrailingNewlineOfARealPatch()
    {
        const string patch = "@@ -0,0 +1,3 @@\n+namespace Demo;\n+\n+public sealed class Widget { }\n";

        var content = UnifiedDiffParser.TryReconstructAddedFileContent(patch);

        content.Should().Be("namespace Demo;\n\npublic sealed class Widget { }\n");
    }

    [Fact]
    public void TryReconstructAddedFileContentSkipsTheFileHeadersOfARawDiff()
    {
        const string patch =
            "diff --git a/src/Demo/Widget.cs b/src/Demo/Widget.cs\n" +
            "new file mode 100644\n" +
            "index 0000000..017fb74\n" +
            "--- /dev/null\n" +
            "+++ b/src/Demo/Widget.cs\n" +
            "@@ -0,0 +1,1 @@\n" +
            "+public sealed class Widget { }\n";

        // The "+++ b/..." header must not be mistaken for an added line of content.
        UnifiedDiffParser.TryReconstructAddedFileContent(patch)
            .Should().Be("public sealed class Widget { }\n");
    }

    [Fact]
    public void TryReconstructAddedFileContentRefusesAPatchThatIsOnlyAFragment()
    {
        const string patch = "@@ -1,3 +1,4 @@\n public class Existing\n {\n+    public int Added;\n }\n";

        // Context lines mean this is a modification; rebuilding from it would invent a file.
        UnifiedDiffParser.TryReconstructAddedFileContent(patch).Should().BeNull();
    }

    [Fact]
    public void TryReconstructAddedFileContentReturnsNullForNothingUsable()
    {
        UnifiedDiffParser.TryReconstructAddedFileContent(null).Should().BeNull();
        UnifiedDiffParser.TryReconstructAddedFileContent(string.Empty).Should().BeNull();
        UnifiedDiffParser.TryReconstructAddedFileContent("@@ -0,0 +0,0 @@\n").Should().BeNull();
    }

    [Fact]
    public void TryReconstructAddedFileContentIgnoresTheNoNewlineMarker()
    {
        const string patch = "@@ -0,0 +1,1 @@\n+last line\n\\ No newline at end of file\n";

        UnifiedDiffParser.TryReconstructAddedFileContent(patch).Should().Be("last line\n");
    }

    [Theory]
    // Real headers GitHub produced for this repository. In each case git's C# regex
    // named a method that does not contain the change, because a tuple or generic
    // return type does not match its pattern and it walked back to an earlier member.
    [InlineData(
        "@@ -135,6 +135,7 @@ private async Task<BuildResult> RunCompileAllAsync(string workspacePath, Cancell",
        "@@ -135,6 +135,7 @@")]
    [InlineData(
        "@@ -122,6 +122,7 @@ internal static bool HasPytestConfig(string workspacePath)",
        "@@ -122,6 +122,7 @@")]
    [InlineData("@@ -1,2 +1,3 @@", "@@ -1,2 +1,3 @@")]
    [InlineData("@@ -0,0 +1 @@ class Foo", "@@ -0,0 +1 @@")]
    public void AnnotateWithLineNumbersDropsTheGuessedFunctionContext(string header, string expected)
    {
        var annotated = UnifiedDiffParser.AnnotateWithLineNumbers(header + "\n+added\n");

        annotated[0].Should().Be(
            expected,
            "the enclosing-member name is a regex guess the model would otherwise read as fact");
    }

    [Fact]
    public void AnnotateWithLineNumbersStillNumbersLinesAfterStrippingContext()
    {
        const string patch = "@@ -10,2 +10,3 @@ private static async Task<(string A, int B)> CaptureAsync(\n context\n+added\n";

        var annotated = UnifiedDiffParser.AnnotateWithLineNumbers(patch);

        annotated[0].Should().Be("@@ -10,2 +10,3 @@");
        annotated[2].Should().Contain("11").And.Contain("added");
    }
}

