namespace ReviewBot.Core.Domain;

public sealed record FileChange(
    string Path,
    string Patch,
    IReadOnlySet<int> CommentableLines,
    long AdditionsCount,
    long DeletionsCount,
    FileChangeStatus Status,
    /// <summary>
    /// False when GitHub returned the file without a text patch — binary content, a file
    /// past its diff size limit, or a pure rename. The file still counts against coverage,
    /// so it is carried through the pipeline and reported rather than dropped on sight.
    /// </summary>
    bool IsReviewable = true);

public enum FileChangeStatus
{
    Added = 0,
    Modified = 1,
    Removed = 2,
    Renamed = 3,
    Copied = 4
}
