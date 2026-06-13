namespace ReviewBot.Persistence.Reviews;

public static class CommentBatching
{
    /// <summary>Splits comments into batches of at most <paramref name="size"/>. Empty input yields no batches.</summary>
    public static IEnumerable<IReadOnlyList<ArchivedComment>> Chunk(IReadOnlyList<ArchivedComment> comments, int size)
    {
        for (var start = 0; start < comments.Count; start += size)
        {
            var count = Math.Min(size, comments.Count - start);
            var batch = new ArchivedComment[count];
            for (var i = 0; i < count; i++)
            {
                batch[i] = comments[start + i];
            }

            yield return batch;
        }
    }
}
