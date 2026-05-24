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
        payload.UserPrompt.Should().Contain("@@ -5 +5 @@\n-return null;\n+return value;");
        payload.UserPrompt.Should().Contain("=== src/Zeta.cs (Modified, +1 -1) ===");
        payload.UserPrompt.IndexOf("=== src/Alpha.cs", StringComparison.Ordinal)
            .Should().BeLessThan(payload.UserPrompt.IndexOf("=== src/Zeta.cs", StringComparison.Ordinal));
        payload.UserPrompt.Should().Contain("0. src/Alpha.cs:5 [medium]\nThis may still return null.");
        payload.UserPrompt.Should().Contain("1. src/Zeta.cs:1 [low]\nThis line is stylistic.");
    }

    [Fact]
    public void SystemPromptContainsSelfCritiqueSchema()
    {
        var payload = SelfCritiquePromptBuilder.Build([], []);

        payload.SystemPrompt.Should().Contain("evaluating a junior reviewer's proposed pull request comments");
        payload.SystemPrompt.Should().Contain("depend on missing context instead of evidence visible in the diff");
        payload.SystemPrompt.Should().Contain("say an implementation is not visible, cannot be verified");
        payload.SystemPrompt.Should().Contain("merely say a call could throw");
        payload.SystemPrompt.Should().Contain("duplicate the same root cause already covered by a clearer comment");
        payload.SystemPrompt.Should().Contain("paste or restate code already visible in the diff");
        payload.SystemPrompt.Should().Contain("\"retained_indices\": [0, 2]");
        payload.SystemPrompt.Should().Contain("\"rationale\": \"string, brief explanation of removals\"");
        payload.SystemPrompt.Should().Contain("The retained_indices array is authoritative");
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

        payload.UserPrompt.Should().Contain("@@ -1 +1 @@\n-old\n+new");
        payload.UserPrompt.Should().NotContain("\0");
        payload.UserPrompt.Should().NotContain("\r");
    }
}
