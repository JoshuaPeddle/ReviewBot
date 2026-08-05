namespace Scale.Repositories;

/// <summary>Promotional discount lookup.</summary>
public sealed class DiscountRepository
{
    private readonly Dictionary<string, decimal> rates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SAVE10"] = 0.10m,
        ["SAVE15"] = 0.15m,
        ["HALFOFF"] = 0.50m,
    };

    /// <summary>
    /// Returns the discount as a <b>fraction</b> of the order total: 0.15 means 15% off.
    /// It is deliberately not a percentage, so callers never divide by 100.
    /// </summary>
    public decimal GetRate(string code) =>
        this.rates.TryGetValue(code, out var rate) ? rate : 0m;
}
