using FluentAssertions;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Prompting;

namespace ReviewBot.Core.Tests.Prompting;

public class PromptBuilderTests
{
    [Fact]
    public void SystemPromptIncludesEachFocusAreaAndSchema()
    {
        var request = CreateRequest(config: ReviewConfig.Default with
        {
            Focus = ["correctness", "security", "tests"]
        });

        var payload = PromptBuilder.Build(request);

        payload.SystemPrompt.Should().Contain("- correctness");
        payload.SystemPrompt.Should().Contain("- security");
        payload.SystemPrompt.Should().Contain("- tests");
        payload.SystemPrompt.Should().Contain("\"summary\": \"string, markdown allowed, 1-3 short paragraphs\"");
        payload.SystemPrompt.Should().Contain("\"comments\": [");
        payload.SystemPrompt.Should().Contain("\"line\": \"integer, must be a commentable line on the new side\"");
    }

    [Fact]
    public void SystemPromptContainsCustomInstructionsWhenSet()
    {
        var request = CreateRequest(config: ReviewConfig.Default with
        {
            Instructions = "Prioritize public API compatibility."
        });

        var payload = PromptBuilder.Build(request);

        payload.SystemPrompt.Should().Contain("Additional instructions:\nPrioritize public API compatibility.");
    }

    [Fact]
    public void UserPromptOrdersFilesAlphabeticallyByPath()
    {
        var request = CreateRequest(files:
        [
            CreateFile(path: "src/Zeta.cs"),
            CreateFile(path: "src/Alpha.cs"),
            CreateFile(path: "src/Beta.cs")
        ]);

        var payload = PromptBuilder.Build(request);

        payload.UserPrompt.Should().ContainAll("=== src/Alpha.cs", "=== src/Beta.cs", "=== src/Zeta.cs");
        payload.UserPrompt.IndexOf("=== src/Alpha.cs", StringComparison.Ordinal)
            .Should().BeLessThan(payload.UserPrompt.IndexOf("=== src/Beta.cs", StringComparison.Ordinal));
        payload.UserPrompt.IndexOf("=== src/Beta.cs", StringComparison.Ordinal)
            .Should().BeLessThan(payload.UserPrompt.IndexOf("=== src/Zeta.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void PatchTruncationAppendsMarkerWhenPatchExceedsConfiguredLimit()
    {
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { MaxPatchLines = 3 }
        };
        var request = CreateRequest(
            config: config,
            files:
            [
                CreateFile(
                    path: "src/Long.cs",
                    patch: "+line 1\n+line 2\n+line 3\n+line 4\n+line 5")
            ]);

        var payload = PromptBuilder.Build(request);

        payload.UserPrompt.Should().Contain("""
```diff
+line 1
+line 2
+line 3
... (truncated, 2 more lines)
```
""");
    }

    [Fact]
    public void PatchesAreSanitizedByRemovingNullBytes()
    {
        var request = CreateRequest(files:
        [
            CreateFile(path: "src/NullByte.cs", patch: "+clean\0line")
        ]);

        var payload = PromptBuilder.Build(request);

        payload.UserPrompt.Should().Contain("+cleanline");
        payload.UserPrompt.Should().NotContain("\0");
    }

    [Fact]
    public void BuildReturnsDeterministicSnapshot()
    {
        var config = ReviewConfig.Default with
        {
            Focus = ["correctness", "security"],
            Instructions = "Be concise and only comment on actionable issues.",
            Review = ReviewConfig.Default.Review with { MaxPatchLines = 4 }
        };
        var request = CreateRequest(
            title: "Tighten review parsing",
            body: "Adds parser validation and fixture coverage.",
            config: config,
            files:
            [
                CreateFile(
                    path: "src/ReviewBot.Core/Zeta.cs",
                    patch: "@@ -1,2 +1,3 @@\n public class Zeta\n+{\n+}\n",
                    additions: 2,
                    deletions: 0),
                CreateFile(
                    path: "src/ReviewBot.Core/Alpha.cs",
                    patch: "@@ -10,2 +10,3 @@\n public class Alpha\n-old\n+new\n+done\n",
                    additions: 2,
                    deletions: 1)
            ]);

        var payload = PromptBuilder.Build(request);

        payload.SystemPrompt.Should().Be("""
You are a senior code reviewer. Review the pull request with particular focus on: correctness, security.

Focus areas:
- correctness
- security

Additional instructions:
Be concise and only comment on actionable issues.

Respond ONLY with a JSON object matching this schema and nothing else. Do not use markdown fences, preambles, or trailing prose.
Schema:
{
  "summary": "string, markdown allowed, 1-3 short paragraphs",
  "comments": [
    {
      "path": "string, must match one of the changed files",
      "line": "integer, must be a commentable line on the new side",
      "severity": "info|warning|error",
      "body": "string, markdown allowed; for fixes use GitHub suggestion blocks"
    }
  ]
}
Omit a comment entirely rather than pick a guessed line, and keep total comments under 25.
""");

        payload.UserPrompt.Should().Be("""
PR Title:
Tighten review parsing

PR Body:
Adds parser validation and fixture coverage.

Changed Files:
=== src/ReviewBot.Core/Alpha.cs (Modified, +2 -1) ===
```diff
@@ -10,2 +10,3 @@
 public class Alpha
-old
+new
... (truncated, 1 more lines)
```

=== src/ReviewBot.Core/Zeta.cs (Modified, +2 -0) ===
```diff
@@ -1,2 +1,3 @@
 public class Zeta
+{
+}
```
""");
    }

    private static ReviewRequest CreateRequest(
        string title = "Add review bot",
        string body = "Please review the changes.",
        ReviewConfig? config = null,
        IReadOnlyList<FileChange>? files = null) =>
        new(
            PrTitle: title,
            PrBody: body,
            BaseSha: "base",
            HeadSha: "head",
            Files: files ?? [CreateFile(path: "src/ReviewBot.Core/Review.cs")],
            Config: config ?? ReviewConfig.Default);

    private static FileChange CreateFile(
        string path,
        string patch = "@@ -1 +1 @@\n-old\n+new",
        long additions = 1,
        long deletions = 1,
        FileChangeStatus status = FileChangeStatus.Modified) =>
        new(
            Path: path,
            Patch: patch,
            CommentableLines: new HashSet<int> { 1 },
            AdditionsCount: additions,
            DeletionsCount: deletions,
            Status: status);
}
