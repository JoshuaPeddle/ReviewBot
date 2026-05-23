using System.Text;
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

        if (grounding?.Language is { } language)
        {
            prompt.Append('\n');
            prompt.Append(BuildGroundingSection(language, grounding.Build));
        }

        prompt.Append("""

Assign a confidence level to each comment based on how certain you are:
- "high": you have seen the code in question and are certain this is a real issue
- "medium": likely an issue but depends on context outside the diff
- "low": speculative or stylistic; you would not block a merge on this alone
""");

        if (config.Review.AgenticContext)
        {
            prompt.Append($"""

You may request up to {config.Review.MaxContextRequests} additional files to review. Include a context_requests array in your response if you need to see referenced types, interfaces, or base classes. Only request files you are confident are relevant.
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
      "line": "integer, must be a commentable line on the new side",
      "severity": "info|warning|error",
      "confidence": "high|medium|low",
      "body": "string, markdown allowed; for fixes use GitHub suggestion blocks"
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

    private static string BuildGroundingSection(LanguageMetadata language, BuildResult? build)
    {
        var sb = new StringBuilder();
        sb.Append("## Project context (verified from repository)\n");

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

        return sb.ToString();
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
            prompt.Append("```diff\n");
            prompt.Append(SanitizeAndTruncatePatch(file.Patch, request.Config.Review.MaxPatchLines));
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

    private static string SanitizeAndTruncatePatch(string patch, int maxPatchLines)
    {
        var sanitizedPatch = patch.Replace("\0", string.Empty, StringComparison.Ordinal);
        var normalizedPatch = sanitizedPatch.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var patchForCounting = normalizedPatch.EndsWith('\n')
            ? normalizedPatch[..^1]
            : normalizedPatch;
        var lines = patchForCounting.Split('\n');

        if (lines.Length <= maxPatchLines)
        {
            return string.Join('\n', lines);
        }

        var retainedLineCount = Math.Max(0, maxPatchLines);
        var omittedLineCount = lines.Length - retainedLineCount;
        var retainedLines = lines.Take(retainedLineCount);

        return string.Join('\n', retainedLines.Append($"... (truncated, {omittedLineCount} more lines)"));
    }
}

public sealed record PromptPayload(string SystemPrompt, string UserPrompt);
