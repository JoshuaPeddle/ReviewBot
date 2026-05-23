using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ReviewBot.GitHub.Auth;

public sealed class CachingInstallationTokenProvider : IInstallationTokenProvider
{
    private static readonly TimeSpan ExpirySafetyMargin = TimeSpan.FromMinutes(1);

    private readonly IMemoryCache cache;
    private readonly IInstallationTokenProvider inner;
    private readonly TimeProvider clock;
    private readonly ILogger<CachingInstallationTokenProvider> logger;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> locks = new();

    public CachingInstallationTokenProvider(
        IMemoryCache cache,
        IInstallationTokenProvider inner,
        TimeProvider clock,
        ILogger<CachingInstallationTokenProvider> logger)
    {
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<InstallationToken> GetTokenAsync(long installationId, CancellationToken ct)
    {
        if (installationId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(installationId), installationId, "Installation ID must be positive.");
        }

        if (TryGetCachedToken(installationId, out var token))
        {
            return token;
        }

        var tokenLock = locks.GetOrAdd(installationId, _ => new SemaphoreSlim(1, 1));
        await tokenLock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (TryGetCachedToken(installationId, out token))
            {
                return token;
            }

            token = await inner.GetTokenAsync(installationId, ct).ConfigureAwait(false);
            CacheToken(installationId, token);
            return token;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private bool TryGetCachedToken(long installationId, out InstallationToken token)
    {
        if (cache.TryGetValue(CacheKey(installationId), out CachedInstallationToken? cached)
            && cached is not null
            && cached.ExpiresAt > clock.GetUtcNow())
        {
            token = cached.Token;
            return true;
        }

        cache.Remove(CacheKey(installationId));
        token = null!;
        return false;
    }

    private void CacheToken(long installationId, InstallationToken token)
    {
        var cacheExpiresAt = token.ExpiresAt - ExpirySafetyMargin;
        if (cacheExpiresAt <= clock.GetUtcNow())
        {
            logger.LogDebug(
                "Skipping cache for installation {InstallationId} token because it expires too soon",
                installationId);
            return;
        }

        cache.Set(CacheKey(installationId), new CachedInstallationToken(token, cacheExpiresAt));
    }

    private static string CacheKey(long installationId) => $"github-installation-token:{installationId}";

    private sealed record CachedInstallationToken(InstallationToken Token, DateTimeOffset ExpiresAt);
}
