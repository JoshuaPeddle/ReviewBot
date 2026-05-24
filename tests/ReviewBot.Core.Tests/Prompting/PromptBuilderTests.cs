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
        payload.SystemPrompt.Should().Contain("\"line\": \"integer, use the NNN from the diff line annotation prefix\"");
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
    public void FullFileContentsAreInsertedBeforeMatchingDiff()
    {
        var request = CreateRequest(
            files:
            [
                CreateFile(path: "src/WithContext.cs"),
                CreateFile(path: "src/DiffOnly.cs")
            ],
            fullFileContents: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/WithContext.cs"] = "public class WithContext\r\n{\r\n    private int value;\0\r\n}"
            });

        var payload = PromptBuilder.Build(request);

        payload.UserPrompt.Should().Contain("""
=== src/WithContext.cs (Modified, +1 -1) ===
### Full file: src/WithContext.cs
```
public class WithContext
{
    private int value;
}
```
```diff
""");
        payload.UserPrompt.Should().NotContain("\0");
        payload.UserPrompt.Should().NotContain("### Full file: src/DiffOnly.cs");
        var fullFileIndex = payload.UserPrompt.IndexOf("### Full file: src/WithContext.cs", StringComparison.Ordinal);
        var matchingDiffIndex = payload.UserPrompt.IndexOf("```diff", fullFileIndex, StringComparison.Ordinal);
        fullFileIndex.Should().BeLessThan(matchingDiffIndex);
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

Assign a confidence level to each comment based on how certain you are:
- "high": you have seen the code in question and are certain this is a real issue
- "medium": likely an issue but depends on context outside the diff
- "low": speculative or stylistic; you would not block a merge on this alone

When an existing code comment explains why a design choice was made, do not flag it as a bug unless you can identify a factual error in the stated reasoning.
Only flag security issues at real trust boundaries — user-supplied HTTP fields, external API responses, untrusted file content. Do not flag internal method parameters passed between layers of the same codebase as injection or path traversal risks.
Each non-deleted diff line is prefixed with its exact new-file line number (format: `+  NNN: code` for added, `   NNN: code` for context). Use that number directly as the `line` field — do not count lines yourself.

Respond ONLY with a JSON object matching this schema and nothing else. Do not use markdown fences, preambles, or trailing prose.
Schema:
{
  "summary": "string, markdown allowed, 1-3 short paragraphs",
  "comments": [
    {
      "path": "string, must match one of the changed files",
      "line": "integer, use the NNN from the diff line annotation prefix",
      "severity": "info|warning|error",
      "confidence": "high|medium|low",
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
    10: public class Alpha
-       old
+   11: new
... (truncated, 1 more lines)
```

=== src/ReviewBot.Core/Zeta.cs (Modified, +2 -0) ===
```diff
@@ -1,2 +1,3 @@
     1: public class Zeta
+    2: {
+    3: }
```
""");
    }

    [Fact]
    public void SystemPromptContainsConfidenceInstructionsBeforeSchema()
    {
        var request = CreateRequest();

        var payload = PromptBuilder.Build(request);

        payload.SystemPrompt.Should().Contain("Assign a confidence level to each comment");
        payload.SystemPrompt.Should().Contain("\"high\": you have seen the code in question");
        payload.SystemPrompt.Should().Contain("\"medium\": likely an issue but depends on context");
        payload.SystemPrompt.Should().Contain("\"low\": speculative or stylistic");
        payload.SystemPrompt.Should().Contain("\"confidence\": \"high|medium|low\"");

        var confidenceIndex = payload.SystemPrompt.IndexOf("Assign a confidence level", StringComparison.Ordinal);
        var schemaIndex = payload.SystemPrompt.IndexOf("Respond ONLY with a JSON", StringComparison.Ordinal);
        confidenceIndex.Should().BeLessThan(schemaIndex);
    }

    [Fact]
    public void AgenticContextEnabledAddsContextRequestInstructionsAndSchema()
    {
        var request = CreateRequest(config: ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with
            {
                AgenticContext = true,
                MaxContextRequests = 3
            }
        });

        var payload = PromptBuilder.Build(request);

        payload.SystemPrompt.Should().Contain("You may request up to 3 additional files to review.");
        payload.SystemPrompt.Should().Contain("Include a context_requests array");
        payload.SystemPrompt.Should().Contain("\"context_requests\": [");
        payload.SystemPrompt.Should().Contain("\"path\": \"string, repo-relative path\"");
        payload.SystemPrompt.Should().Contain("\"reason\": \"optional string\"");
    }

    [Fact]
    public void AgenticContextDisabledOmitsContextRequestSchema()
    {
        var request = CreateRequest(config: ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { AgenticContext = false }
        });

        var payload = PromptBuilder.Build(request);

        payload.SystemPrompt.Should().NotContain("context_requests");
        payload.SystemPrompt.Should().NotContain("additional files to review");
    }

    [Fact]
    public void ContextEnrichedRequestContainsFetchedFiles()
    {
        var request = CreateRequest(files:
        [
            CreateFile(path: "src/Review.cs", patch: "@@ -1 +1 @@\n- old\n+ new")
        ]);
        var initialResult = new ReviewResult("Initial summary.", []);

        var payload = PromptBuilder.BuildContextEnrichedRequest(
            request,
            initialResult,
            [("src/Contracts/IReviewStore.cs", "public interface IReviewStore\n{\n    Task SaveAsync();\n}")]);

        payload.SystemPrompt.Should().Contain("This is the final review pass after additional context was fetched.");
        payload.SystemPrompt.Should().NotContain("context_requests");
        payload.UserPrompt.Should().Contain("## Additional context");
        payload.UserPrompt.Should().Contain("### src/Contracts/IReviewStore.cs");
        payload.UserPrompt.Should().Contain("""
```
public interface IReviewStore
{
    Task SaveAsync();
}
```
""");
    }

    [Fact]
    public void ContextEnrichedRequestContainsInitialSummaryAndComments()
    {
        var request = CreateRequest();
        var initialResult = new ReviewResult(
            "Initial summary.",
            [
                new InlineComment(
                    Path: "src/Review.cs",
                    Line: 12,
                    Side: "RIGHT",
                    Body: "Check this contract.",
                    Severity: Severity.Warning,
                    Confidence: Confidence.Medium)
            ]);

        var payload = PromptBuilder.BuildContextEnrichedRequest(
            request,
            initialResult,
            [("src/Contracts/IReviewStore.cs", "public interface IReviewStore {}")]);

        payload.UserPrompt.Should().Contain("Initial review summary:\nInitial summary.");
        payload.UserPrompt.Should().Contain("Initial review comments:\n0. src/Review.cs:12 [warning, medium] Check this contract.");
    }

    [Fact]
    public void NoGroundingProducesNoGroundingSection()
    {
        var request = CreateRequest();

        var payload = PromptBuilder.Build(request);

        payload.SystemPrompt.Should().NotContain("## Project context");
    }

    [Fact]
    public void GroundingWithNullLanguageProducesNoGroundingSection()
    {
        var request = CreateRequest() with
        {
            Grounding = new GroundingContext(Language: null, Build: null, Tests: null)
        };

        var payload = PromptBuilder.Build(request);

        payload.SystemPrompt.Should().NotContain("## Project context");
    }

    [Fact]
    public void DotNetGroundingInjectsVersionSectionBeforeResponseSchema()
    {
        var request = CreateRequest() with
        {
            Grounding = new GroundingContext(
                Language: new LanguageMetadata(
                    LanguageId: "dotnet",
                    LanguageVersion: "10.0",
                    ToolchainVersion: "10.0.100",
                    Facts: ["LangVersion: latest", "Nullable: enable"]),
                Build: null,
                Tests: null)
        };

        var payload = PromptBuilder.Build(request);

        payload.SystemPrompt.Should().Contain("## Project context (verified from repository)");
        payload.SystemPrompt.Should().Contain("- Language: C# (.NET 10.0)");
        payload.SystemPrompt.Should().Contain("- Toolchain: .NET SDK 10.0.100");
        payload.SystemPrompt.Should().Contain("- LangVersion: latest");
        payload.SystemPrompt.Should().Contain("- Nullable: enable");
        payload.SystemPrompt.Should().Contain("- Build: not verified (syntax claims cannot be confirmed)");

        var groundingIndex = payload.SystemPrompt.IndexOf("## Project context", StringComparison.Ordinal);
        var schemaIndex = payload.SystemPrompt.IndexOf("Respond ONLY with a JSON", StringComparison.Ordinal);
        groundingIndex.Should().BeLessThan(schemaIndex);
    }

    [Fact]
    public void PythonGroundingInjectsVersionSection()
    {
        var request = CreateRequest() with
        {
            Grounding = new GroundingContext(
                Language: new LanguageMetadata(
                    LanguageId: "python",
                    LanguageVersion: "3.12",
                    ToolchainVersion: null,
                    Facts: ["requires-python: >=3.12", "mypy configured"]),
                Build: null,
                Tests: null)
        };

        var payload = PromptBuilder.Build(request);

        payload.SystemPrompt.Should().Contain("- Language: Python 3.12");
        payload.SystemPrompt.Should().Contain("- requires-python: >=3.12");
        payload.SystemPrompt.Should().Contain("- mypy configured");
        payload.SystemPrompt.Should().NotContain("Toolchain");
    }

    [Fact]
    public void BuildSuccessGroundingStatesConfirmedSyntax()
    {
        var request = CreateRequest() with
        {
            Grounding = new GroundingContext(
                Language: new LanguageMetadata("dotnet", "10.0", null, []),
                Build: new BuildResult(Success: true, Warnings: 0, Errors: 0, Output: string.Empty),
                Tests: null)
        };

        var payload = PromptBuilder.Build(request);

        payload.SystemPrompt.Should().Contain(
            "- Build: SUCCESS (0 warnings, 0 errors) — all syntax in changed files is confirmed valid");
    }

    [Fact]
    public void BuildFailureGroundingStatesFailure()
    {
        var request = CreateRequest() with
        {
            Grounding = new GroundingContext(
                Language: new LanguageMetadata("dotnet", "10.0", null, []),
                Build: new BuildResult(Success: false, Warnings: 0, Errors: 3, Output: "error output"),
                Tests: null)
        };

        var payload = PromptBuilder.Build(request);

        payload.SystemPrompt.Should().Contain(
            "- Build: FAILED (3 errors) — see build output below");
    }

    [Fact]
    public void GroundingWithGitHubChecksIncludesVerificationWithoutLanguage()
    {
        var request = CreateRequest() with
        {
            Grounding = new GroundingContext(
                Language: null,
                Build: null,
                Tests: new TestResult(1, 1, 0, "- check tests: failure", "github_checks"))
        };

        var payload = PromptBuilder.Build(request);

        payload.SystemPrompt.Should().Contain("## Project verification");
        payload.SystemPrompt.Should().Contain("- Checks: FAILED (1 passed, 1 failed, 0 skipped)");
        payload.SystemPrompt.Should().Contain("Checks output:");
        payload.SystemPrompt.Should().Contain("- check tests: failure");
    }

    [Fact]
    public void GroundingWithPassingLocalTestsIncludesTestsLine()
    {
        var request = CreateRequest() with
        {
            Grounding = new GroundingContext(
                Language: new LanguageMetadata("dotnet", "10.0", null, []),
                Build: new BuildResult(true, 0, 0, "ok"),
                Tests: new TestResult(42, 0, 3, "local tests ok"))
        };

        var payload = PromptBuilder.Build(request);

        payload.SystemPrompt.Should().Contain("- Tests: PASSED (42 passed, 0 failed, 3 skipped)");
        payload.SystemPrompt.Should().Contain("Tests output:");
    }

    [Fact]
    public void GroundingWithFailingLocalTestsIncludesFailedLine()
    {
        var request = CreateRequest() with
        {
            Grounding = new GroundingContext(
                Language: new LanguageMetadata("dotnet", "10.0", null, []),
                Build: new BuildResult(true, 0, 0, "ok"),
                Tests: new TestResult(Passed: 38, Failed: 4, Skipped: 0, Output: "test output"))
        };

        var payload = PromptBuilder.Build(request);

        payload.SystemPrompt.Should().Contain("- Tests: FAILED (38 passed, 4 failed, 0 skipped)");
        payload.SystemPrompt.Should().Contain("existing behavior may have regressed");
    }

    [Fact]
    public void GroundingWithNullTestsOmitsTestsLine()
    {
        var request = CreateRequest() with
        {
            Grounding = new GroundingContext(
                Language: new LanguageMetadata("dotnet", "10.0", null, []),
                Build: new BuildResult(true, 0, 0, "ok"),
                Tests: null)
        };

        var payload = PromptBuilder.Build(request);

        payload.SystemPrompt.Should().Contain("## Project context");
        payload.SystemPrompt.Should().NotContain("Tests:");
        payload.SystemPrompt.Should().NotContain("Checks:");
    }

    private static ReviewRequest CreateRequest(
        string title = "Add review bot",
        string body = "Please review the changes.",
        ReviewConfig? config = null,
        IReadOnlyList<FileChange>? files = null,
        IReadOnlyDictionary<string, string>? fullFileContents = null) =>
        new(
            PrTitle: title,
            PrBody: body,
            BaseSha: "base",
            HeadSha: "head",
            Files: files ?? [CreateFile(path: "src/ReviewBot.Core/Review.cs")],
            Config: config ?? ReviewConfig.Default,
            FullFileContents: fullFileContents);

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
