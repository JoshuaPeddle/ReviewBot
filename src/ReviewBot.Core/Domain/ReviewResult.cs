namespace ReviewBot.Core.Domain;

public sealed record ReviewResult
{
    public ReviewResult(
        string Summary,
        IReadOnlyList<InlineComment> Comments,
        IReadOnlyList<ContextRequest>? ContextRequests = null)
    {
        this.Summary = Summary;
        this.Comments = Comments;
        this.ContextRequests = ContextRequests ?? Array.Empty<ContextRequest>();
    }

    public string Summary { get; init; }

    public IReadOnlyList<InlineComment> Comments { get; init; }

    public IReadOnlyList<ContextRequest> ContextRequests { get; init; }

    public Llm.LlmTokenUsage? TokenUsage { get; init; }

    public string? RawLlmResponse { get; init; }
}

public sealed record InlineComment(
    string Path,
    int Line,
    string Side,
    string Body,
    Severity Severity,
    Confidence Confidence = Confidence.High,
    VerificationStatus Verification = VerificationStatus.Unverified);

public enum VerificationStatus
{
    // No ground truth either way; the finding is posted as-is (current behaviour).
    Unverified = 0,

    // An independent compiler/analyzer diagnostic corroborates the finding.
    Verified = 1
}

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
