namespace ReviewBot.GitHub.Pulls;

public sealed record ChangedFilesResult(
    IReadOnlyList<string> Paths,
    bool IsComplete);
