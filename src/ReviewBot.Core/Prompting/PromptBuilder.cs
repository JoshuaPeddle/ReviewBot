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
                "This is the final review pass after additional context was fetched. For each numbered initial comment, decide keep, revise, or drop based on the new context: keep means re-emit unchanged; revise means re-emit with updated wording (only one version, not both); drop means omit because the added files disprove the concern. Then add any new comments the new context now supports. Do not return the same concern twice."),
            UserPrompt: BuildContextEnrichedUserPrompt(request, initialResult, fetchedFiles));
    }

    private static string BuildSystemPrompt(
        ReviewConfig config,
        GroundingContext? grounding,
        string? preSchemaInstruction = null)
    {
        var prompt = new StringBuilder();

        prompt.Append("You are a senior code reviewer reviewing a pull request.\n\n");

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

Assign a severity level to each comment:
- "error": a correctness, security, or data-loss bug that should block merge
- "warning": a likely defect, reliability hazard, or maintainability concern worth fixing before merge
- "info": a minor style, naming, or nit that is safe to ignore

Assign a confidence level to each comment based on how certain you are:
- "high": you have seen the code in question and are certain this is a real issue
- "medium": likely an issue but depends on context outside the diff
- "low": weak but evidence-backed; you would not block a merge on this alone

What a good comment looks like:
GOOD (specific, ties to a visible line, names the value, gives a fix direction):
  "`request.Email` on line 47 is interpolated into the SQL string. It originates from the HTTP body, so treat it as untrusted and use a parameter."
BAD (speculative, asks for vague confirmation about code not in the diff):
  "Make sure the email is validated somewhere before this point."
Omit comments that read like the BAD example.

Comment quality rules:
- Only report actionable concerns. Do not leave praise, positive feedback, confirmations that code is correct, or comments whose purpose is to validate that a change looks good.
- Review the pull request behavior, not the review/eval harness. Do not comment that a fixture, expected finding, or expected.yaml correctly models or requires a result.
- Inline comments should be concise: state the issue, why it matters, and the smallest useful fix direction in 1-3 sentences.
- Do not paste, quote, or restate code that is already visible in the diff. The GitHub review UI already shows the relevant code.
- Do not include code fences, pseudocode, or example implementations unless you can provide a short GitHub suggestion block that is an exact replacement for the commented lines.
- If a concern depends on code that is not present, request that file through the additional context mechanism when available. If you cannot request or see the needed context, omit the comment.
- Do not leave "not visible in this diff", "cannot verify", or "make sure this is handled elsewhere" comments.
- Do not speculate about a referenced method's return type, async behavior, side effects, or contract. If that contract is needed and unavailable, request context when available; otherwise omit the comment.
- Do not flag that a call "could throw" unless the diff changed an error-handling boundary, removed existing handling, violates a visible contract, or creates an observable reliability regression.
- If several lines share the same root cause, leave one comment at the best line instead of repeating the same concern on each call site.

When an existing code comment explains why a design choice was made, do not flag it as a bug unless you can identify a factual error in the stated reasoning.
Only flag security issues at real trust boundaries — user-supplied HTTP fields, external API responses, untrusted file content. Do not flag internal method parameters passed between layers of the same codebase as injection or path traversal risks.
Each non-deleted diff line is prefixed with its exact new-file line number (format: `+  NNN: code` for added, `   NNN: code` for context). Use that number directly as the `line` field — do not count lines yourself.
""");

        if (config.Review.AgenticContext)
        {
            prompt.Append($"""

You may request up to {config.Review.MaxContextRequests} additional files to review. Include a context_requests array in your response if you need to see referenced types, interfaces, base classes, or helper implementations before making a comment. Only request files you are confident are relevant. Still emit any comments you can make confidently from the current diff in this same response. For concerns whose validity depends on a requested file, omit the comment from this pass; you will see the file content in the next pass and can decide then.
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
      "line": "integer, copy the NNN from the diff line annotation prefix verbatim; never count lines yourself",
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
Omit a comment entirely rather than pick a guessed line or provide positive feedback. Aim for the 3 to 7 highest-impact issues; never exceed 15. When in doubt, omit.
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
                    ? $"Build: SUCCESS ({build.Warnings} warnings, {build.Errors} errors), code compiles"
                    : $"Build: FAILED ({build.Errors} errors), see build output below";
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
            ? "no existing test regressed"
            : "existing tests are now failing";

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
        sb.Append(" skipped), ");
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

        if (request is { ChunkIndex: not null, TotalChunks: not null } &&
            request.TotalChunks > 1)
        {
            prompt.Append("\n\nReview scope:\n(reviewing chunk ");
            prompt.Append(request.ChunkIndex.Value);
            prompt.Append(" of ");
            prompt.Append(request.TotalChunks.Value);
            prompt.Append(')');
        }

        if (AppendRepositoryContext(prompt, request.RepositoryContext))
        {
            prompt.Append("\nChanged Files:\n");
        }
        else
        {
            prompt.Append("\n\nChanged Files:\n");
        }

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

    private static bool AppendRepositoryContext(
        StringBuilder prompt,
        IReadOnlyList<RepositoryContextSnippet>? repositoryContext)
    {
        if (repositoryContext is null || repositoryContext.Count == 0)
        {
            return false;
        }

        prompt.Append("\n\n## Repository context\n");
        foreach (var snippet in repositoryContext
            .OrderBy(snippet => snippet.Path, StringComparer.Ordinal)
            .ThenBy(snippet => snippet.StartLine)
            .ThenBy(snippet => snippet.EndLine))
        {
            prompt.Append("### ");
            prompt.Append(snippet.Path);
            prompt.Append(" lines ");
            prompt.Append(snippet.StartLine);
            prompt.Append('-');
            prompt.Append(snippet.EndLine);
            prompt.Append("\n```\n");
            prompt.Append(SanitizeFetchedContent(snippet.Content));
            prompt.Append("\n```\n");
        }

        return true;
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
