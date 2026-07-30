using System.Text;
using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Prompting;

public static class SelfCritiquePromptBuilder
{
    /// <summary>
    /// Builds the critique pass over <paramref name="proposedComments"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="repositoryContext"/> and <paramref name="fullFileContents"/> must be
    /// the same evidence the review pass saw. A critic given only the diff has no way to
    /// tell a finding derived from a retrieved callee body apart from one invented about
    /// code that is not there, and its instructions tell it to delete the latter — so it
    /// deletes both. Measured on the 27-fixture corpus, that cost 8 of 22 true positives.
    /// </remarks>
    public static PromptPayload Build(
        IReadOnlyList<FileChange> files,
        IReadOnlyList<InlineComment> proposedComments,
        IReadOnlyList<RepositoryContextSnippet>? repositoryContext = null,
        IReadOnlyDictionary<string, string>? fullFileContents = null,
        int maxPatchLines = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(proposedComments);

        return new PromptPayload(
            SystemPrompt: BuildSystemPrompt(),
            UserPrompt: BuildUserPrompt(
                files, proposedComments, repositoryContext, fullFileContents, maxPatchLines));
    }

    private static string BuildSystemPrompt() =>
        """
You are a senior code reviewer evaluating a junior reviewer's proposed pull request comments for accuracy and usefulness.

The evidence below is everything the junior reviewer saw: the diff, and — when present — the full content of changed files and a "Repository context" section holding definitions pulled from elsewhere in the repository. A comment grounded in the repository context is grounded in evidence, not speculation. Judge every comment against all of it, not the diff alone.

Remove comments that:
- target a line not present in the diff
- claim a bug that the diff or the provided context shows is already handled
- flag valid modern syntax as invalid
- express pure style preference with no correctness, security, reliability, or maintainability implication
- rest on an assumption about code that appears in none of the evidence provided
- say an implementation is not visible, cannot be verified, or should be checked elsewhere
- guess at a referenced method's return type, async behavior, side effects, or contract when no provided evidence shows it
- praise, validate, or confirm that code is correct instead of identifying an actionable concern
- discuss whether a fixture, expected finding, or expected.yaml correctly models or requires a result
- merely say a call could throw without a changed error-handling boundary, visible contract violation, or observable reliability regression
- duplicate the same root cause already covered by a clearer comment
- paste or restate code already visible in the diff instead of giving concise review guidance

Keep every comment whose claim the provided evidence supports, including one that reasons across files. Removing a real defect is a worse error than keeping a marginal comment.

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
        IReadOnlyList<InlineComment> proposedComments,
        IReadOnlyList<RepositoryContextSnippet>? repositoryContext,
        IReadOnlyDictionary<string, string>? fullFileContents,
        int maxPatchLines)
    {
        var prompt = new StringBuilder();

        if (PromptSections.AppendRepositoryContext(prompt, repositoryContext))
        {
            prompt.Append('\n');
        }

        prompt.Append("Changed Files:\n");
        PromptSections.AppendChangedFiles(prompt, files, fullFileContents, maxPatchLines);

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
}
