namespace ReviewBot.Core.Domain;

public sealed record FileChange(
    string Path,
    string Patch,
    IReadOnlySet<int> CommentableLines,
    long AdditionsCount,
    long DeletionsCount,
    FileChangeStatus Status);

public enum FileChangeStatus
{
    Added = 0,
    Modified = 1,
    Removed = 2,
    Renamed = 3,
    Copied = 4
}
