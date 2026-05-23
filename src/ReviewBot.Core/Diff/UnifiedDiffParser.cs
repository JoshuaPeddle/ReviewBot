using System.Globalization;
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

    private static IEnumerable<string> SplitLines(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return normalized.Split('\n');
    }
}
