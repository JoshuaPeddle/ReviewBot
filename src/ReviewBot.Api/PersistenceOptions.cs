namespace ReviewBot.Api;

public sealed record PersistenceOptions
{
    public const string SectionName = "Persistence";

    public string ConnectionString { get; set; } = "Data Source=reviewbot.db";
}
