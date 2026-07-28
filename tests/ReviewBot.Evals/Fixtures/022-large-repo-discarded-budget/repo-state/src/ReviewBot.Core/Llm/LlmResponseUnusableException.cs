namespace ReviewBot.Core.Llm;

/// <summary>
/// Thrown when a provider returned a response that cannot be turned into a review —
/// an empty body, or JSON that survived neither the initial parse nor the repair pass.
/// </summary>
/// <remarks>
/// Failing the job is deliberate. The alternative, returning an empty
/// <see cref="ReviewBot.Core.Domain.ReviewResult"/>, gets posted as a successful review
/// with no findings, which tells the PR author "nothing wrong here" when in truth
/// nothing was reviewed. A failed job is visible in the logs and leaves the PR
/// unreviewed; a false clean review is silent and actively misleading.
/// </remarks>
public sealed class LlmResponseUnusableException : Exception
{
    public LlmResponseUnusableException(string message)
        : base(message)
    {
    }

    public LlmResponseUnusableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
