using ReviewBot.Core.Domain;
using ReviewBot.Core.Options;

namespace ReviewBot.GitHub.Pulls;

public sealed class PullRequestFetcher
{
    public Task<IReadOnlyList<FileChange>> FetchFilesAsync(PaginationOptions options)
    {
        if (options.PageSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.PageSize));
        }

        return FetchPageAsync(perPage: options.PageSize);
    }

    private static Task<IReadOnlyList<FileChange>> FetchPageAsync(int perPage) =>
        Task.FromResult<IReadOnlyList<FileChange>>([]);
}
