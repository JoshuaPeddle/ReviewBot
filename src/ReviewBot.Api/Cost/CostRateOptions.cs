namespace ReviewBot.Api.Cost;

public sealed class CostRateOptions
{
    public const string SectionName = "CostRates";

    public Dictionary<string, CostRate> Rates { get; set; } = [];
}

public sealed class CostRate
{
    /// <summary>Cost in USD per 1 million input (prompt) tokens.</summary>
    public decimal InputPer1M { get; set; }

    /// <summary>Cost in USD per 1 million output (completion) tokens.</summary>
    public decimal OutputPer1M { get; set; }
}
