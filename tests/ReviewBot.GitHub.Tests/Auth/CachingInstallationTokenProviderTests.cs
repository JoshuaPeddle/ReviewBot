using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReviewBot.GitHub.Auth;

namespace ReviewBot.GitHub.Tests.Auth;

public class CachingInstallationTokenProviderTests
{
    [Fact]
    public async Task GetTokenAsyncReturnsCachedTokenOnSecondCall()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 5, 23, 12, 0, 0, TimeSpan.Zero));
        var token = new InstallationToken("first", clock.GetUtcNow().AddMinutes(10));
        var inner = Substitute.For<IInstallationTokenProvider>();
        inner.GetTokenAsync(123, Arg.Any<CancellationToken>()).Returns(token);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = CreateProvider(cache, inner, clock);

        var first = await provider.GetTokenAsync(123, CancellationToken.None);
        var second = await provider.GetTokenAsync(123, CancellationToken.None);

        first.Should().Be(token);
        second.Should().Be(token);
        await inner.Received(1).GetTokenAsync(123, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTokenAsyncRefreshesExpiredCachedToken()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 5, 23, 12, 0, 0, TimeSpan.Zero));
        var firstToken = new InstallationToken("first", clock.GetUtcNow().AddMinutes(5));
        var refreshedToken = new InstallationToken("second", clock.GetUtcNow().AddMinutes(20));
        var inner = Substitute.For<IInstallationTokenProvider>();
        inner.GetTokenAsync(123, Arg.Any<CancellationToken>())
            .Returns(firstToken, refreshedToken);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = CreateProvider(cache, inner, clock);

        await provider.GetTokenAsync(123, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(4).Add(TimeSpan.FromSeconds(1)));
        var refreshed = await provider.GetTokenAsync(123, CancellationToken.None);

        refreshed.Should().Be(refreshedToken);
        await inner.Received(2).GetTokenAsync(123, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTokenAsyncDoesNotCacheTokensInsideSafetyMargin()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 5, 23, 12, 0, 0, TimeSpan.Zero));
        var firstToken = new InstallationToken("first", clock.GetUtcNow().AddSeconds(30));
        var secondToken = new InstallationToken("second", clock.GetUtcNow().AddMinutes(5));
        var inner = Substitute.For<IInstallationTokenProvider>();
        inner.GetTokenAsync(123, Arg.Any<CancellationToken>())
            .Returns(firstToken, secondToken);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = CreateProvider(cache, inner, clock);

        var first = await provider.GetTokenAsync(123, CancellationToken.None);
        var second = await provider.GetTokenAsync(123, CancellationToken.None);

        first.Should().Be(firstToken);
        second.Should().Be(secondToken);
        await inner.Received(2).GetTokenAsync(123, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTokenAsyncDeduplicatesConcurrentRequestsForSameInstallation()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 5, 23, 12, 0, 0, TimeSpan.Zero));
        var token = new InstallationToken("deduped", clock.GetUtcNow().AddMinutes(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var inner = Substitute.For<IInstallationTokenProvider>();
        inner.GetTokenAsync(123, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            Interlocked.Increment(ref callCount);
            started.TrySetResult();
            await release.Task;
            return token;
        });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = CreateProvider(cache, inner, clock);

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => provider.GetTokenAsync(123, CancellationToken.None))
            .ToArray();

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        callCount.Should().Be(1);

        release.SetResult();
        var tokens = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2));

        tokens.Should().OnlyContain(value => value == token);
        await inner.Received(1).GetTokenAsync(123, Arg.Any<CancellationToken>());
    }

    private static CachingInstallationTokenProvider CreateProvider(
        IMemoryCache cache,
        IInstallationTokenProvider inner,
        TimeProvider clock) =>
        new(
            cache,
            inner,
            clock,
            NullLogger<CachingInstallationTokenProvider>.Instance);
}
