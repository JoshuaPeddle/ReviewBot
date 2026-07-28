using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Llm;

public static class ReviewResultMerger
{
    public static ReviewResult Merge(IReadOnlyList<ReviewResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        if (results.Count == 0)
        {
            return new ReviewResult(string.Empty, []);
        }

        var comments = results
            .SelectMany(result => result.Comments)
            .GroupBy(
                comment => new CommentKey(comment.Path, comment.Line, comment.Side))
            .Select(group => group
                .OrderByDescending(comment => comment.Severity)
                .ThenByDescending(comment => comment.Confidence)
                .First())
            .OrderBy(comment => comment.Path, StringComparer.Ordinal)
            .ThenBy(comment => comment.Line)
            .ThenBy(comment => comment.Side, StringComparer.Ordinal)
            .ToArray();

        var contextRequests = results
            .SelectMany(result => result.ContextRequests)
            .GroupBy(request => request.Path, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(request => request.Path, StringComparer.Ordinal)
            .ToArray();

        var tokenUsage = results
            .Select(r => r.TokenUsage)
            .Aggregate((LlmTokenUsage?)null, (acc, u) => acc is null ? u : u is null ? acc : acc.Add(u));

        return new ReviewResult(string.Empty, comments, contextRequests) { TokenUsage = tokenUsage };
    }

    private sealed record CommentKey(string Path, int Line, string Side);
}
