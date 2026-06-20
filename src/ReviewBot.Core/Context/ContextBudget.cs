namespace ReviewBot.Core.Context;

/// <summary>
/// Derives budget values that should scale with the detected model context
/// window, so one configuration behaves sensibly across models from 8K to 200K.
/// </summary>
public static class ContextBudget
{
    // A reserve below this can't hold a useful structured review reply.
    public const int MinViableReserveTokens = 512;

    // The response reserve may not exceed this fraction of the context window:
    // a fixed reserve (default 4096) that is fine at 32K would starve the prompt
    // on an 8K model, so we cap it relative to the window the server reports.
    private const int MaxReserveContextDivisor = 4;

    /// <summary>
    /// Clamps the configured response reserve to the detected context window.
    /// Only ever reduces the reserve: a caller that explicitly opts out with 0
    /// keeps 0, and the common 4096-at-32K case is unchanged (4096 ≤ 32768/4).
    /// </summary>
    public static int ResolveResponseReserveTokens(int configuredReserveTokens, int contextWindowTokens)
    {
        // 0 (or negative) means "no reserve" — preserve that intent verbatim.
        if (configuredReserveTokens <= 0)
        {
            return configuredReserveTokens;
        }

        var ceiling = Math.Max(MinViableReserveTokens, contextWindowTokens / MaxReserveContextDivisor);
        return Math.Min(configuredReserveTokens, ceiling);
    }
}
