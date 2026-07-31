namespace ReviewBot.Core.Context;

public sealed class ModelContextOptions
{
    public const string SectionName = "ModelContext";

    public Dictionary<string, int> Limits { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Host-wide safety ceiling applied even when a live provider advertises a larger window.
    /// Null leaves the provider value uncapped.
    /// </summary>
    public int? MaxContextWindowTokens { get; set; }
}
