using Octokit;

namespace ReviewBot.GitHub.Pulls;

public sealed class ReviewPostException : Exception
{
    public ReviewPostException(
        string message,
        int acceptedCommentCount,
        int droppedCommentCount,
        ApiValidationException innerException)
        : base(message, innerException)
    {
        AcceptedCommentCount = acceptedCommentCount;
        DroppedCommentCount = droppedCommentCount;
    }

    public int AcceptedCommentCount { get; }

    public int DroppedCommentCount { get; }
}
