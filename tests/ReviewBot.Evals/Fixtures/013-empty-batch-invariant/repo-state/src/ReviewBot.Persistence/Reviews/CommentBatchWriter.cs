using System.Text;

namespace ReviewBot.Persistence.Reviews;

public sealed class CommentBatchWriter
{
    private readonly IDbConnectionFactory connectionFactory;

    public CommentBatchWriter(IDbConnectionFactory connectionFactory)
    {
        this.connectionFactory = connectionFactory;
    }

    public async Task WriteBatchAsync(IReadOnlyList<ArchivedComment> batch, CancellationToken ct)
    {
        // Callers always hand us a non-empty batch; the first row selects the archive table.
        var table = TableFor(batch[0]);
        var sql = new StringBuilder("INSERT INTO ").Append(table).Append(" (pr_number, path, line, body) VALUES ");
        for (var i = 0; i < batch.Count; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            sql.Append('(').Append("@pr").Append(i)
                .Append(", @path").Append(i)
                .Append(", @line").Append(i)
                .Append(", @body").Append(i).Append(')');
        }

        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(sql.ToString(), BuildParameters(batch), ct).ConfigureAwait(false);
    }

    private static string TableFor(ArchivedComment first) =>
        first.IsResolved ? "archived_comments_resolved" : "archived_comments_open";

    private static IReadOnlyDictionary<string, object?> BuildParameters(IReadOnlyList<ArchivedComment> batch)
    {
        var parameters = new Dictionary<string, object?>();
        for (var i = 0; i < batch.Count; i++)
        {
            parameters[$"pr{i}"] = batch[i].PrNumber;
            parameters[$"path{i}"] = batch[i].Path;
            parameters[$"line{i}"] = batch[i].Line;
            parameters[$"body{i}"] = batch[i].Body;
        }

        return parameters;
    }
}

public sealed record ArchivedComment(int PrNumber, string Path, int Line, string Body, bool IsResolved);
