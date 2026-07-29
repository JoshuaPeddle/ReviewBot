using FluentAssertions;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Prompting;

namespace ReviewBot.Core.Tests.Prompting;

public class SelfCritiquePromptBuilderTests
{
    [Fact]
    public void PromptContainsDiffAndEachProposedCommentWithIndex()
    {
        FileChange[] files =
        [
            new(
                Path: "src/Zeta.cs",
                Patch: "@@ -1 +1 @@\n-old\n+new",
                CommentableLines: new HashSet<int> { 1 },
                AdditionsCount: 1,
                DeletionsCount: 1,
                Status: FileChangeStatus.Modified),
            new(
                Path: "src/Alpha.cs",
                Patch: "@@ -5 +5 @@\n-return null;\n+return value;",
                CommentableLines: new HashSet<int> { 5 },
                AdditionsCount: 1,
                DeletionsCount: 1,
                Status: FileChangeStatus.Modified)
        ];
        InlineComment[] comments =
        [
            new("src/Alpha.cs", 5, "RIGHT", "This may still return null.", Severity.Warning, Confidence.Medium),
            new("src/Zeta.cs", 1, "RIGHT", "This line is stylistic.", Severity.Info, Confidence.Low)
        ];

        var payload = SelfCritiquePromptBuilder.Build(files, comments);

        payload.UserPrompt.Should().Contain("=== src/Alpha.cs (Modified, +1 -1) ===");
        payload.UserPrompt.Should().Contain("=== src/Zeta.cs (Modified, +1 -1) ===");
        payload.UserPrompt.IndexOf("=== src/Alpha.cs", StringComparison.Ordinal)
            .Should().BeLessThan(payload.UserPrompt.IndexOf("=== src/Zeta.cs", StringComparison.Ordinal));
        payload.UserPrompt.Should().Contain("0. src/Alpha.cs:5 [medium]\nThis may still return null.");
        payload.UserPrompt.Should().Contain("1. src/Zeta.cs:1 [low]\nThis line is stylistic.");
    }

    /// <summary>
    /// The critic's first removal rule is "targets a line not present in the diff", which
    /// it cannot apply to an unannotated patch. It now gets the same numbering the review
    /// pass was given, so a comment's line can actually be checked against the diff.
    /// </summary>
    [Fact]
    public void PromptAnnotatesDiffLinesWithNewFileLineNumbers()
    {
        FileChange[] files =
        [
            new(
                Path: "src/Alpha.cs",
                Patch: "@@ -5 +5 @@\n-return null;\n+return value;",
                CommentableLines: new HashSet<int> { 5 },
                AdditionsCount: 1,
                DeletionsCount: 1,
                Status: FileChangeStatus.Modified)
        ];

        var payload = SelfCritiquePromptBuilder.Build(files, []);

        payload.UserPrompt.Should().Contain("+    5: return value;");
    }

    /// <summary>
    /// The whole point of the change: a critic shown only the diff deletes findings that
    /// reason across files, because it cannot tell them apart from invention.
    /// </summary>
    [Fact]
    public void PromptIncludesRepositoryContextAndFullFileContentWhenProvided()
    {
        FileChange[] files =
        [
            new(
                Path: "src/Caller.cs",
                Patch: "@@ -1 +1 @@\n+Scheduler.Start(0);",
                CommentableLines: new HashSet<int> { 1 },
                AdditionsCount: 1,
                DeletionsCount: 0,
                Status: FileChangeStatus.Modified)
        ];
        RepositoryContextSnippet[] repositoryContext =
        [
            new("src/Scheduler.cs", 10, 14, "public void Start(int interval) => timer.Change(interval, interval);")
        ];
        var fullFileContents = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/Caller.cs"] = "class Caller { void Go() => Scheduler.Start(0); }"
        };

        var payload = SelfCritiquePromptBuilder.Build(files, [], repositoryContext, fullFileContents);

        payload.UserPrompt.Should().Contain("## Repository context");
        payload.UserPrompt.Should().Contain("### src/Scheduler.cs lines 10-14");
        payload.UserPrompt.Should().Contain("timer.Change(interval, interval)");
        payload.UserPrompt.Should().Contain("### Full file: src/Caller.cs");
        payload.UserPrompt.Should().Contain("class Caller { void Go() => Scheduler.Start(0); }");
        payload.UserPrompt.IndexOf("## Repository context", StringComparison.Ordinal)
            .Should().BeLessThan(payload.UserPrompt.IndexOf("Changed Files:", StringComparison.Ordinal));
    }

    [Fact]
    public void PromptOmitsRepositoryContextSectionWhenNoneProvided()
    {
        var payload = SelfCritiquePromptBuilder.Build([], []);

        payload.UserPrompt.Should().NotContain("## Repository context");
    }

    [Fact]
    public void SystemPromptTellsTheCriticToJudgeAgainstAllProvidedEvidence()
    {
        var payload = SelfCritiquePromptBuilder.Build([], []);

        payload.SystemPrompt.Should().Contain("evaluating a junior reviewer's proposed pull request comments");
        payload.SystemPrompt.Should().Contain("Judge every comment against all of it, not the diff alone");
        payload.SystemPrompt.Should().Contain("rest on an assumption about code that appears in none of the evidence provided");
        payload.SystemPrompt.Should().Contain("Keep every comment whose claim the provided evidence supports");
        payload.SystemPrompt.Should().Contain("Removing a real defect is a worse error than keeping a marginal comment");
        payload.SystemPrompt.Should().Contain("say an implementation is not visible, cannot be verified");
        payload.SystemPrompt.Should().Contain("praise, validate, or confirm that code is correct");
        payload.SystemPrompt.Should().Contain("expected.yaml correctly models or requires");
        payload.SystemPrompt.Should().Contain("merely say a call could throw");
        payload.SystemPrompt.Should().Contain("duplicate the same root cause already covered by a clearer comment");
        payload.SystemPrompt.Should().Contain("paste or restate code already visible in the diff");
        payload.SystemPrompt.Should().Contain("\"retained_indices\": [0, 2]");
        payload.SystemPrompt.Should().Contain("\"rationale\": \"string, brief explanation of removals\"");
        payload.SystemPrompt.Should().Contain("The retained_indices array is authoritative");
    }

    /// <summary>
    /// The old prompt told the critic to remove anything that "depends on missing context
    /// instead of evidence visible in the diff". With retrieval snippets now in front of
    /// it, that instruction deleted correct cross-file findings.
    /// </summary>
    [Fact]
    public void SystemPromptNoLongerRejectsFindingsForReachingBeyondTheDiff()
    {
        var payload = SelfCritiquePromptBuilder.Build([], []);

        payload.SystemPrompt.Should().NotContain("depend on missing context instead of evidence visible in the diff");
    }

    [Fact]
    public void PromptSanitizesPatchNullBytesAndLineEndings()
    {
        FileChange[] files =
        [
            new(
                Path: "src/LineEndings.cs",
                Patch: "@@ -1 +1 @@\r\n-old\0\r+new",
                CommentableLines: new HashSet<int> { 1 },
                AdditionsCount: 1,
                DeletionsCount: 1,
                Status: FileChangeStatus.Modified)
        ];

        var payload = SelfCritiquePromptBuilder.Build(files, []);

        payload.UserPrompt.Should().Contain("+    1: new");
        payload.UserPrompt.Should().NotContain("\0");
        payload.UserPrompt.Should().NotContain("\r");
    }
}
