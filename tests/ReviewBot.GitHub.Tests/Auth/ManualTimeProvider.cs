namespace ReviewBot.GitHub.Tests.Auth;

internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset now = now;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan delta) => now += delta;
}
