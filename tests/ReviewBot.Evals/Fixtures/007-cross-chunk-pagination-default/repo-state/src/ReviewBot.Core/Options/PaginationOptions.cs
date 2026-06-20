namespace ReviewBot.Core.Options;

public sealed record PaginationOptions
{
    public int PageSize { get; init; } = 0;
}
