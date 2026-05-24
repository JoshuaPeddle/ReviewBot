using System.Text;
using ReviewBot.Core.Diff;
using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Prompting;

public static class PromptBuilder
{
    public static PromptPayload Build(ReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new PromptPayload(
            SystemPrompt: BuildSystemPrompt(request.Config, request.Grounding),
            UserPrompt: BuildUserPrompt(request));
    }

    public static PromptPayload BuildContextEnrichedRequest(
        ReviewRequest request,
        ReviewResult initialResult,
        IReadOnlyList<(string Path, string Content)> fetchedFiles)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(initialResult);
        ArgumentNullException.ThrowIfNull(fetchedFiles);

        var finalPassConfig = request.Config with
        {
            Review = request.Config.Review with { AgenticContext = false }
        };

        return new PromptPayload(
            SystemPrompt: BuildSystemPrompt(
                finalPassConfig,
                request.Grounding,
                "This is the final review pass after additional context was fetched. Re-emit all valid comments from the first pass plus any new comments informed by the additional context. Omit any first-pass comments disproven by the added files."),
            UserPrompt: BuildContextEnrichedUserPrompt(request, initialResult, fetchedFiles));
    }

    private static string BuildSystemPrompt(
        ReviewConfig config,
        GroundingContext? grounding,
        string? preSchemaInstruction = null)
    {
        var prompt = new StringBuilder();

        prompt.Append("You are a senior code reviewer. Review the pull request with particular focus on: ");
        prompt.AppendJoin(", ", config.Focus);
        prompt.Append(".\n\n");

        prompt.Append("Focus areas:\n");
        foreach (var focusArea in config.Focus)
        {
            prompt.Append("- ");
            prompt.Append(focusArea);
            prompt.Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(config.Instructions))
        {
            prompt.Append("\nAdditional instructions:\n");
            prompt.Append(config.Instructions);
            prompt.Append('\n');
        }

        if (grounding is { Language: not null } or { Tests: not null })
        {
            prompt.Append('\n');
            prompt.Append(BuildGroundingSection(grounding.Language, grounding.Build, grounding.Tests));
        }

        prompt.Append("""

Assign a confidence level to each comment based on how certain you are:
- "high": you have seen the code in question and are certain this is a real issue
- "medium": likely an issue but depends on context outside the diff
- "low": speculative or stylistic; you would not block a merge on this alone

Comment quality rules:
- Inline comments should be concise: state the issue, why it matters, and the smallest useful fix direction in 1-3 sentences.
- Do not paste, quote, or restate code that is already visible in the diff. The GitHub review UI already shows the relevant code.
- Do not include code fences, pseudocode, or example implementations unless you can provide a short GitHub suggestion block that is an exact replacement for the commented lines.
- If a concern depends on code that is not present, request that file through the additional context mechanism when available. If you cannot request or see the needed context, omit the comment.
- Do not leave "not visible in this diff", "cannot verify", or "make sure this is handled elsewhere" comments.
- Do not flag that a call "could throw" unless the diff changed an error-handling boundary, removed existing handling, violates a visible contract, or creates an observable reliability regression.
- If several lines share the same root cause, leave one comment at the best line instead of repeating the same concern on each call site.

When an existing code comment explains why a design choice was made, do not flag it as a bug unless you can identify a factual error in the stated reasoning.
Only flag security issues at real trust boundaries — user-supplied HTTP fields, external API responses, untrusted file content. Do not flag internal method parameters passed between layers of the same codebase as injection or path traversal risks.
Each non-deleted diff line is prefixed with its exact new-file line number (format: `+  NNN: code` for added, `   NNN: code` for context). Use that number directly as the `line` field — do not count lines yourself.
""");

        if (config.Review.AgenticContext)
        {
            prompt.Append($"""

You may request up to {config.Review.MaxContextRequests} additional files to review. Include a context_requests array in your response if you need to see referenced types, interfaces, base classes, or helper implementations before making a comment. Only request files you are confident are relevant.
""");
        }

        if (!string.IsNullOrWhiteSpace(preSchemaInstruction))
        {
            prompt.Append("\n\n");
            prompt.Append(preSchemaInstruction);
        }

        prompt.Append('\n');
        prompt.Append("""

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
      "body": "string, markdown allowed; concise review comment, no copied diff code"
    }
  ]
""");

        prompt.Append('\n');
        if (config.Review.AgenticContext)
        {
            prompt.Append("""
,
  "context_requests": [
    { "path": "string, repo-relative path", "reason": "optional string" }
  ]
""");
        }

        prompt.Append("""
}
Omit a comment entirely rather than pick a guessed line, and keep total comments under 25.
""");

        return prompt.ToString();
    }

    private static string BuildGroundingSection(LanguageMetadata? language, BuildResult? build, TestResult? tests)
    {
        var sb = new StringBuilder();
        sb.Append(language is null
            ? "## Project verification\n"
            : "## Project context (verified from repository)\n");

        if (language is not null)
        {
            var displayName = language.LanguageId switch
            {
                "dotnet" => $"C# (.NET {language.LanguageVersion})",
                "python" => $"Python {language.LanguageVersion}",
                _ => $"{language.LanguageId} {language.LanguageVersion}"
            };
            sb.Append($"- Language: {displayName}\n");

            if (language.ToolchainVersion is not null)
            {
                var toolchainLabel = language.LanguageId switch
                {
                    "dotnet" => $".NET SDK {language.ToolchainVersion}",
                    _ => language.ToolchainVersion
                };
                sb.Append($"- Toolchain: {toolchainLabel}\n");
            }

            foreach (var fact in language.Facts)
                sb.Append($"- {fact}\n");

            if (build is not null)
            {
                var buildLine = build.Success
                    ? $"Build: SUCCESS ({build.Warnings} warnings, {build.Errors} errors) — all syntax in changed files is confirmed valid"
                    : $"Build: FAILED ({build.Errors} errors) — see build output below";
                sb.Append($"- {buildLine}\n");
            }
            else
            {
                sb.Append("- Build: not verified (syntax claims cannot be confirmed)\n");
            }
        }

        if (tests is not null)
            AppendTestResult(sb, tests);

        return sb.ToString();
    }

    private static void AppendTestResult(StringBuilder sb, TestResult tests)
    {
        var label = string.Equals(tests.Source, "github_checks", StringComparison.Ordinal)
            ? "Checks"
            : "Tests";
        var status = tests.Failed == 0 ? "PASSED" : "FAILED";
        var detail = tests.Failed == 0
            ? "existing behavior confirmed"
            : "existing behavior may have regressed";

        sb.Append("- ");
        sb.Append(label);
        sb.Append(": ");
        sb.Append(status);
        sb.Append(" (");
        sb.Append(tests.Passed);
        sb.Append(" passed, ");
        sb.Append(tests.Failed);
        sb.Append(" failed, ");
        sb.Append(tests.Skipped);
        sb.Append(" skipped) — ");
        sb.Append(detail);
        sb.Append('\n');

        if (!string.IsNullOrWhiteSpace(tests.Output))
        {
            sb.Append(label);
            sb.Append(" output:\n```text\n");
            sb.Append(tests.Output);
            sb.Append("\n```\n");
        }
    }

    private static string BuildUserPrompt(ReviewRequest request)
    {
        var prompt = new StringBuilder();

        prompt.Append("PR Title:\n");
        prompt.Append(request.PrTitle);
        prompt.Append("\n\nPR Body:\n");
        prompt.Append(request.PrBody);
        prompt.Append("\n\nChanged Files:\n");

        var orderedFiles = request.Files.OrderBy(file => file.Path, StringComparer.Ordinal);
        foreach (var file in orderedFiles)
        {
            prompt.Append("=== ");
            prompt.Append(file.Path);
            prompt.Append(" (");
            prompt.Append(file.Status);
            prompt.Append(", +");
            prompt.Append(file.AdditionsCount);
            prompt.Append(" -");
            prompt.Append(file.DeletionsCount);
            prompt.Append(") ===\n");
            if (request.FullFileContents?.TryGetValue(file.Path, out var fullFileContent) == true)
            {
                prompt.Append("### Full file: ");
                prompt.Append(file.Path);
                prompt.Append("\n```\n");
                prompt.Append(SanitizeFetchedContent(fullFileContent));
                prompt.Append("\n```\n");
            }

            prompt.Append("```diff\n");
            prompt.Append(AnnotateAndTruncatePatch(file.Patch, request.Config.Review.MaxPatchLines));
            prompt.Append("\n```\n\n");
        }

        return prompt.ToString().TrimEnd();
    }

    private static string BuildContextEnrichedUserPrompt(
        ReviewRequest request,
        ReviewResult initialResult,
        IReadOnlyList<(string Path, string Content)> fetchedFiles)
    {
        var prompt = new StringBuilder();

        prompt.Append(BuildUserPrompt(request));
        prompt.Append("\n\nInitial review summary:\n");
        prompt.Append(initialResult.Summary);
        prompt.Append("\n\nInitial review comments:\n");

        if (initialResult.Comments.Count == 0)
        {
            prompt.Append("None.");
        }
        else
        {
            for (var index = 0; index < initialResult.Comments.Count; index++)
            {
                var comment = initialResult.Comments[index];
                prompt.Append(index);
                prompt.Append(". ");
                prompt.Append(comment.Path);
                prompt.Append(':');
                prompt.Append(comment.Line);
                prompt.Append(" [");
                prompt.Append(comment.Severity.ToString().ToLowerInvariant());
                prompt.Append(", ");
                prompt.Append(comment.Confidence.ToString().ToLowerInvariant());
                prompt.Append("] ");
                prompt.Append(comment.Body);
                prompt.Append('\n');
            }
        }

        prompt.Append("\n\n## Additional context\n");
        foreach (var fetchedFile in fetchedFiles)
        {
            prompt.Append("### ");
            prompt.Append(fetchedFile.Path);
            prompt.Append("\n```\n");
            prompt.Append(SanitizeFetchedContent(fetchedFile.Content));
            prompt.Append("\n```\n");
        }

        return prompt.ToString().TrimEnd();
    }

    private static string SanitizeFetchedContent(string content)
    {
        return content
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static string AnnotateAndTruncatePatch(string patch, int maxPatchLines)
    {
        var lines = UnifiedDiffParser.AnnotateWithLineNumbers(patch);

        if (lines.Length <= maxPatchLines)
            return string.Join('\n', lines);

        var omittedCount = lines.Length - maxPatchLines;
        return string.Join('\n',
            lines.Take(maxPatchLines).Append($"... (truncated, {omittedCount} more lines)"));
    }
}

public sealed record PromptPayload(string SystemPrompt, string UserPrompt);
