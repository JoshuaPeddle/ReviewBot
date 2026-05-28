using System.Diagnostics;

namespace ReviewBot.Api.Otel;

internal static class ReviewBotActivitySource
{
    internal const string SourceName = "ReviewBot";
    internal static readonly ActivitySource Instance = new(SourceName, "1.0.0");
}
