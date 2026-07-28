namespace ReviewBot.Core.Context;

/// <summary>
/// Derives budget values that should scale with the detected model context
/// window, so one configuration behaves sensibly across models from 8K to 200K.
/// </summary>
public static class ContextBudget
{
    // Floor for the reserve: below this the model cannot fit a structured review
    // reply, so the request is wasted however much prompt room it buys.
    public const int MinViableReserveTokens = 512;

    // The response reserve may not exceed this fraction of the context window:
    // a fixed reserve (default 4096) that is fine at 32K would starve the prompt
    // on an 8K model, so we cap it relative to the window the server reports.
    private const int MaxReserveContextDivisor = 4;

    // ...and may not fall below this fraction of it. A fixed 4096 that is generous at
    // 32K is far too little on a 100K reasoning model: such a model spends its output
    // allowance on chain-of-thought before emitting any answer, so too small a reserve
    // yields an empty response. Observed on Qwen3.6-27B at 100K, which burned all 4096
    // reserved tokens reasoning and returned no content while 61K of content budget sat
    // unused. Scaling the floor with the window spends budget we were not using anyway.
    private const int MinReserveContextDivisor = 8;

    /// <summary>
    /// Fits the configured response reserve to the detected context window, scaling it
    /// both down (so it cannot starve the prompt on a small model) and up (so it cannot
    /// starve the answer on a large one).
    /// </summary>
    /// <remarks>
    /// A caller that explicitly opts out with 0 keeps 0. Windows of 32K and below are
    /// unchanged from the previous behaviour — at 32K the floor is 4096, exactly the
    /// default — so this only moves the needle on the large-window models where the
    /// fixed reserve was the problem.
    /// </remarks>
    public static int ResolveResponseReserveTokens(int configuredReserveTokens, int contextWindowTokens)
    {
        // 0 (or negative) means "no reserve" — preserve that intent verbatim.
        if (configuredReserveTokens <= 0)
        {
            return configuredReserveTokens;
        }

        var ceiling = Math.Max(MinViableReserveTokens, contextWindowTokens / MaxReserveContextDivisor);
        var floor = Math.Min(ceiling, contextWindowTokens / MinReserveContextDivisor);
        return Math.Clamp(configuredReserveTokens, Math.Max(MinViableReserveTokens, floor), ceiling);
    }
}
