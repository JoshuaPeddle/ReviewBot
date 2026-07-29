using System.Text;
using ReviewBot.Core.Diff;
using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Prompting;

/// <summary>
/// Prompt fragments shared by the review prompt and the self-critique prompt.
/// </summary>
/// <remarks>
/// The critic has to see the same evidence the reviewer did. While it saw only the diff it
/// deleted every finding derived from retrieval or full-file context: cross-file evidence
/// the reviewer had read, to the critic, as an unsupported claim about code it could not
/// see. Rendering both prompts from here keeps the two from drifting apart again.
/// </remarks>
internal static class PromptSections
{
    /// <summary>
    /// Renders the changed files, each optionally preceded by its full head content.
    /// </summary>
    public static void AppendChangedFiles(
        StringBuilder prompt,
        IReadOnlyList<FileChange> files,
        IReadOnlyDictionary<string, string>? fullFileContents,
        int maxPatchLines)
    {
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

            if (fullFileContents?.TryGetValue(file.Path, out var fullFileContent) == true)
            {
                prompt.Append("### Full file: ");
                prompt.Append(file.Path);
                prompt.Append("\n```\n");
                prompt.Append(Sanitize(fullFileContent));
                prompt.Append("\n```\n");
            }

            prompt.Append("```diff\n");
            prompt.Append(AnnotateAndTruncatePatch(file.Patch, maxPatchLines));
            prompt.Append("\n```\n\n");
        }
    }

    /// <summary>
    /// Renders retrieved repository snippets. Returns false when there were none.
    /// </summary>
    public static bool AppendRepositoryContext(
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
            prompt.Append(Sanitize(snippet.Content));
            prompt.Append("\n```\n");
        }

        return true;
    }

    /// <summary>
    /// Normalises line endings and breaks any literal triple-backtick run with a
    /// zero-width space, so file content cannot close the surrounding fence and inject
    /// prompt instructions to the model.
    /// </summary>
    public static string Sanitize(string content) =>
        content
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("```", "``​`", StringComparison.Ordinal);

    /// <summary>
    /// Prefixes each non-deleted line with its new-file line number, then truncates to
    /// <paramref name="maxPatchLines"/>.
    /// </summary>
    /// <remarks>
    /// The critic gets the same annotation as the reviewer on purpose: its first removal
    /// rule is "targets a line not present in the diff", which it cannot judge against an
    /// unannotated patch.
    /// </remarks>
    public static string AnnotateAndTruncatePatch(string patch, int maxPatchLines)
    {
        var lines = UnifiedDiffParser.AnnotateWithLineNumbers(patch);

        if (lines.Length <= maxPatchLines)
        {
            return string.Join('\n', lines);
        }

        var omittedCount = lines.Length - maxPatchLines;
        return string.Join('\n',
            lines.Take(maxPatchLines).Append($"... (truncated, {omittedCount} more lines)"));
    }
}
