using System.Text;
using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Prompting;

public static class SelfCritiquePromptBuilder
{
    public static PromptPayload Build(
        IReadOnlyList<FileChange> files,
        IReadOnlyList<InlineComment> proposedComments)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(proposedComments);

        return new PromptPayload(
            SystemPrompt: BuildSystemPrompt(),
            UserPrompt: BuildUserPrompt(files, proposedComments));
    }

    private static string BuildSystemPrompt() =>
        """
You are a senior code reviewer evaluating a junior reviewer's proposed pull request comments for accuracy and usefulness.

Remove comments that:
- target a line not present in the diff
- claim a bug that is clearly handled elsewhere in the same diff
- flag valid modern syntax as invalid
- express pure style preference with no correctness, security, reliability, or maintainability implication
- depend on missing context instead of evidence visible in the diff
- say an implementation is not visible, cannot be verified, or should be checked elsewhere
- praise, validate, or confirm that code is correct instead of identifying an actionable concern
- merely say a call could throw without a changed error-handling boundary, visible contract violation, or observable reliability regression
- duplicate the same root cause already covered by a clearer comment
- paste or restate code already visible in the diff instead of giving concise review guidance

Respond ONLY with a JSON object matching this schema and nothing else. Do not use markdown fences, preambles, or trailing prose.
Schema:
{
  "retained_indices": [0, 2],
  "rationale": "string, brief explanation of removals"
}

The retained_indices array is authoritative. Do not rewrite or re-emit the comments.
""";

    private static string BuildUserPrompt(
        IReadOnlyList<FileChange> files,
        IReadOnlyList<InlineComment> proposedComments)
    {
        var prompt = new StringBuilder();

        prompt.Append("Changed Files:\n");
        foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
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
            prompt.Append(SanitizePatch(file.Patch));
            prompt.Append("\n```\n\n");
        }

        prompt.Append("Proposed Comments:\n");
        for (var index = 0; index < proposedComments.Count; index++)
        {
            var comment = proposedComments[index];
            prompt.Append(index);
            prompt.Append(". ");
            prompt.Append(comment.Path);
            prompt.Append(':');
            prompt.Append(comment.Line);
            prompt.Append(" [");
            prompt.Append(comment.Confidence.ToString().ToLowerInvariant());
            prompt.Append("]\n");
            prompt.Append(comment.Body);
            prompt.Append("\n\n");
        }

        return prompt.ToString().TrimEnd();
    }

    private static string SanitizePatch(string patch) =>
        patch.Replace("\0", string.Empty, StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
}
