namespace ReviewBot.Persistence.Reviews;

public sealed class ReviewCommentArchiver
{
    private readonly CommentBatchWriter writer;

    public ReviewCommentArchiver(CommentBatchWriter writer)
    {
        this.writer = writer;
    }

    public async Task ArchiveAsync(IReadOnlyList<ArchivedComment> comments, CancellationToken ct)
    {
        await writer.WriteBatchAsync(comments, ct).ConfigureAwait(false);
    }
}
