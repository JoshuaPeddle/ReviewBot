namespace ReviewBot.Core.Domain;

public sealed record ReviewResult(
    string Summary,
    IReadOnlyList<InlineComment> Comments);

public sealed record InlineComment(
    string Path,
    int Line,
    string Side,
    string Body,
    Severity Severity,
    Confidence Confidence = Confidence.High);

public enum Severity
{
    Info = 0,
    Warning = 1,
    Error = 2
}

public enum Confidence
{
    Low = 0,
    Medium = 1,
    High = 2
}
