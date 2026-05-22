namespace ReviewBot.Core.Domain;

public sealed record ReviewResult(
    string Summary,
    IReadOnlyList<InlineComment> Comments);

public sealed record InlineComment(
    string Path,
    int Line,
    string Side,
    string Body,
    Severity Severity);

public enum Severity
{
    Info = 0,
    Warning = 1,
    Error = 2
}
