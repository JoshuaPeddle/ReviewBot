using ReviewBot.Core.Diff;
using ReviewBot.Core.Domain;

namespace ReviewBot.Evals;

public static class EvalDiffParser
{
    public static IReadOnlyList<FileChange> ParseFiles(string diffPatch)
    {
        ArgumentNullException.ThrowIfNull(diffPatch);

        var files = new List<FileChange>();
        ParsedFile? current = null;
        var patchLines = new List<string>();

        foreach (var line in NormalizeLines(diffPatch))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FlushCurrent();
                current = new ParsedFile(ParsePathFromDiffHeader(line), FileChangeStatus.Modified);
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (line.StartsWith("new file mode ", StringComparison.Ordinal))
            {
                current = current with { Status = FileChangeStatus.Added };
                continue;
            }

            if (line.StartsWith("deleted file mode ", StringComparison.Ordinal))
            {
                current = current with { Status = FileChangeStatus.Removed };
                continue;
            }

            if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                current = current with { Status = FileChangeStatus.Renamed };
                continue;
            }

            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                current = current with { Path = line["+++ b/".Length..] };
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal) || patchLines.Count > 0)
            {
                patchLines.Add(line);
            }
        }

        FlushCurrent();
        return files;

        void FlushCurrent()
        {
            if (current is null)
            {
                return;
            }

            var patch = string.Join('\n', patchLines);
            var additions = patchLines.Count(IsAddedLine);
            var deletions = patchLines.Count(IsDeletedLine);
            files.Add(new FileChange(
                current.Path,
                patch,
                UnifiedDiffParser.GetCommentableLines(patch),
                additions,
                deletions,
                current.Status));

            patchLines.Clear();
        }
    }

    private static string ParsePathFromDiffHeader(string line)
    {
        var marker = " b/";
        var index = line.LastIndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? line : line[(index + marker.Length)..];
    }

    private static bool IsAddedLine(string line) =>
        line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal);

    private static bool IsDeletedLine(string line) =>
        line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal);

    private static IEnumerable<string> NormalizeLines(string value) =>
        value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private sealed record ParsedFile(string Path, FileChangeStatus Status);
}
