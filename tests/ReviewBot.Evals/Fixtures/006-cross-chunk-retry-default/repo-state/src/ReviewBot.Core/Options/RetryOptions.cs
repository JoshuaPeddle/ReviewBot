namespace ReviewBot.Core.Options;

public sealed record RetryOptions
{
    public int MaxAttempts { get; init; } = 0;
}
