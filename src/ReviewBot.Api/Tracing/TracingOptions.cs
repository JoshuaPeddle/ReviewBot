namespace ReviewBot.Api.Tracing;

public sealed class TracingOptions
{
    public const string SectionName = "Tracing";

    public bool Enabled { get; set; } = false;

    public bool IncludePrompts { get; set; } = true;

    public int MaxDiskMb { get; set; } = 500;

    public int RetentionDays { get; set; } = 14;

    public string TracesDir { get; set; } = "traces";
}
