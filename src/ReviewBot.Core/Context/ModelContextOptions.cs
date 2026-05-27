namespace ReviewBot.Core.Context;

public sealed class ModelContextOptions
{
    public const string SectionName = "ModelContext";

    public Dictionary<string, int> Limits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
