using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ReviewBot.Api.Tracing;

public sealed class JsonReviewTraceWriter : IReviewTraceWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly TracingOptions options;
    private readonly ILogger<JsonReviewTraceWriter> logger;

    public JsonReviewTraceWriter(
        IOptions<TracingOptions> options,
        ILogger<JsonReviewTraceWriter> logger)
    {
        this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task WriteAsync(ReviewTrace trace, CancellationToken ct = default)
    {
        if (!options.Enabled)
        {
            return;
        }

        try
        {
            var dir = Path.Combine(options.TracesDir, trace.Owner, trace.Repo);
            Directory.CreateDirectory(dir);
            var fileName = $"{trace.PrNumber}-{trace.DeliveryId}.json";
            var filePath = Path.Combine(dir, fileName);
            var tempPath = filePath + ".tmp";

            await using (var stream = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, trace, SerializerOptions, ct).ConfigureAwait(false);
            }

            File.Move(tempPath, filePath, overwrite: true);

            logger.LogDebug(
                "Review trace written for {DeliveryId} to {FilePath}",
                trace.DeliveryId,
                filePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Failed to write review trace for delivery {DeliveryId} ({Owner}/{Repo}#{PrNumber})",
                trace.DeliveryId,
                trace.Owner,
                trace.Repo,
                trace.PrNumber);
        }
    }
}
