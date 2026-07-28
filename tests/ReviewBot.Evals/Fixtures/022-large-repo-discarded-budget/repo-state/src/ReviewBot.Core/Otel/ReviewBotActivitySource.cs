using System.Diagnostics;

namespace ReviewBot.Core.Otel;

public static class ReviewBotActivitySource
{
    public const string SourceName = "ReviewBot";
    public static readonly ActivitySource Instance = new(SourceName, "1.0.0");
}
