using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ReviewBot.Core.Diff;

public static class UnifiedDiffParser
{
    private static readonly Regex HunkHeaderPattern = new(
        @"^@@\s+-\d+(?:,\d+)?\s+\+(?<newStart>\d+)(?:,(?<newCount>\d+))?\s+@@(?:\s.*)?$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static IReadOnlySet<int> GetCommentableLines(string? patch)
    {
        var commentableLines = new HashSet<int>();

        if (string.IsNullOrWhiteSpace(patch))
        {
            return commentableLines;
        }

        int? nextNewLine = null;

        foreach (var line in SplitLines(patch))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                nextNewLine = ParseNewStartLine(line);
                continue;
            }

            if (nextNewLine is null || line.StartsWith('\\'))
            {
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            switch (line[0])
            {
                case '+':
                case ' ':
                    commentableLines.Add(nextNewLine.Value);
                    nextNewLine++;
                    break;

                case '-':
                    break;

                default:
                    break;
            }
        }

        return commentableLines;
    }

    private static int ParseNewStartLine(string hunkHeader)
    {
        var match = HunkHeaderPattern.Match(hunkHeader);
        if (!match.Success)
        {
            throw new FormatException($"Malformed unified diff hunk header: {hunkHeader}");
        }

        return int.Parse(
            match.Groups["newStart"].Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture);
    }

    public static string[] AnnotateWithLineNumbers(string? patch)
    {
        if (string.IsNullOrWhiteSpace(patch))
            return [];

        var normalized = patch
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var toParse = normalized.EndsWith('\n') ? normalized[..^1] : normalized;
        var rawLines = toParse.Split('\n');
        var result = new string[rawLines.Length];
        int? nextNewLine = null;

        for (var i = 0; i < rawLines.Length; i++)
        {
            var line = rawLines[i];

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                nextNewLine = ParseNewStartLine(line);
                result[i] = line;
                continue;
            }

            if (line.Length == 0 || line.StartsWith('\\') || nextNewLine is null)
            {
                result[i] = line;
                continue;
            }

            switch (line[0])
            {
                case '+':
                    result[i] = $"+{nextNewLine.Value,5}: {line[1..]}";
                    nextNewLine++;
                    break;
                case ' ':
                    result[i] = $" {nextNewLine.Value,5}: {line[1..]}";
                    nextNewLine++;
                    break;
                case '-':
                    result[i] = $"-       {line[1..]}";
                    break;
                default:
                    result[i] = line;
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Reconstructs the full content of a newly added file from its patch, where every
    /// line is an addition. Returns null for any patch that is not a pure addition.
    /// </summary>
    /// <remarks>
    /// Lets analysis run on an added file without fetching or cloning it: the patch
    /// already holds the whole thing. Anything that is not an added line, a hunk header,
    /// a file header or a "\ No newline" marker means this is a fragment rather than a
    /// whole file, and reconstructing from it would produce a file that never existed —
    /// so it gives up instead.
    ///
    /// Blank entries are skipped rather than rejected. A patch normally ends with a
    /// newline, so splitting yields a trailing empty string; treating that as "not an
    /// added line" rejected essentially every added file, which is what the first version
    /// of this did — caught by ReviewBot reviewing its own PR.
    /// </remarks>
    public static string? TryReconstructAddedFileContent(string? patch)
    {
        if (string.IsNullOrEmpty(patch))
        {
            return null;
        }

        var content = new StringBuilder();
        foreach (var line in SplitLines(patch))
        {
            if (line.Length == 0 || IsHunkOrFileHeader(line) || line.StartsWith('\\'))
            {
                continue;
            }

            if (!line.StartsWith('+'))
            {
                // A context or deleted line: this patch is a fragment, not a whole file.
                return null;
            }

            content.Append(line[1..]).Append('\n');
        }

        return content.Length == 0 ? null : content.ToString();
    }

    /// <summary>
    /// True for hunk headers and the file headers that wrap a diff. GitHub's file API
    /// omits the latter, but a patch read from a <c>.diff</c> carries them, and its
    /// <c>+++ b/path</c> line would otherwise be taken for an added line of content.
    /// </summary>
    private static bool IsHunkOrFileHeader(string line) =>
        line.StartsWith("@@", StringComparison.Ordinal) ||
        line.StartsWith("diff --git ", StringComparison.Ordinal) ||
        line.StartsWith("index ", StringComparison.Ordinal) ||
        line.StartsWith("new file mode ", StringComparison.Ordinal) ||
        line.StartsWith("deleted file mode ", StringComparison.Ordinal) ||
        line.StartsWith("similarity index ", StringComparison.Ordinal) ||
        line.StartsWith("rename ", StringComparison.Ordinal) ||
        line.StartsWith("--- ", StringComparison.Ordinal) ||
        line.StartsWith("+++ ", StringComparison.Ordinal);

    private static IEnumerable<string> SplitLines(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return normalized.Split('\n');
    }
}
