using System.Text;
using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Prompting;

public static class PromptBuilder
{
    public static PromptPayload Build(ReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new PromptPayload(
            SystemPrompt: BuildSystemPrompt(request.Config),
            UserPrompt: BuildUserPrompt(request));
    }

    private static string BuildSystemPrompt(ReviewConfig config)
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
      "body": "string, markdown allowed; for fixes use GitHub suggestion blocks"
    }
  ]
}
Omit a comment entirely rather than pick a guessed line, and keep total comments under 25.
""");

        return prompt.ToString();
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
