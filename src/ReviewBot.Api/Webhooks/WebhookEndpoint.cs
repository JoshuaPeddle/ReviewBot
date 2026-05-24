using System.Text.Json;
using Microsoft.Extensions.Options;
using ReviewBot.Core.Idempotency;
using ReviewBot.Core.Jobs;

namespace ReviewBot.Api.Webhooks;

public static class WebhookEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapWebhookEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/webhook", HandleAsync);

        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        IOptions<WebhookOptions> options,
        IReviewJobQueue queue,
        IDeliveryStore deliveryStore,
        ILogger<WebhookEndpointMarker> logger,
        CancellationToken ct)
    {
        var deliveryId = request.Headers["X-GitHub-Delivery"].ToString();
        var body = await ReadBodyAsync(request, ct);

        if (!WebhookSignatureValidator.IsValid(
                options.Value.Secret,
                body,
                request.Headers["X-Hub-Signature-256"].ToString()))
        {
            logger.LogWarning("Rejected webhook delivery {DeliveryId}: invalid signature", deliveryId);
            return Results.Unauthorized();
        }

        var eventName = request.Headers["X-GitHub-Event"].ToString();
        if (!string.Equals(eventName, "pull_request", StringComparison.Ordinal))
        {
            return Results.NoContent();
        }

        PullRequestEvent? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PullRequestEvent>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Rejected webhook delivery {DeliveryId}: malformed JSON", deliveryId);
            return Results.BadRequest();
        }

        if (payload is null || !HasRequiredFields(payload))
        {
            logger.LogWarning("Rejected webhook delivery {DeliveryId}: missing required pull_request fields", deliveryId);
            return Results.BadRequest();
        }

        if (!ShouldEnqueue(payload))
        {
            return Results.NoContent();
        }

        if (!await deliveryStore.TryRecordAsync(deliveryId, ct).ConfigureAwait(false))
        {
            logger.LogInformation("Skipped duplicate webhook delivery {DeliveryId}", deliveryId);
            return Results.Ok();
        }

        var job = new ReviewJob(
            DeliveryId: deliveryId,
            InstallationId: payload.Installation.Id,
            Owner: payload.Repository.Owner.Login,
            Repo: payload.Repository.Name,
            PrNumber: payload.PullRequest.Number,
            HeadSha: payload.PullRequest.Head.Sha,
            Reason: payload.Action);

        await queue.EnqueueAsync(job, ct);
        logger.LogInformation(
            "Accepted webhook delivery {DeliveryId} for {Owner}/{Repo}#{PrNumber} because of {Reason}",
            job.DeliveryId,
            job.Owner,
            job.Repo,
            job.PrNumber,
            job.Reason);

        return Results.Accepted();
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private static bool ShouldEnqueue(PullRequestEvent payload) =>
        payload.Action is "opened" or "reopened" or "synchronize";

    private static bool HasRequiredFields(PullRequestEvent payload)
    {
        return payload.Installation.Id > 0 &&
               payload.PullRequest.Number > 0 &&
               !string.IsNullOrWhiteSpace(payload.PullRequest.Head.Sha) &&
               !string.IsNullOrWhiteSpace(payload.Repository.Name) &&
               !string.IsNullOrWhiteSpace(payload.Repository.Owner.Login);
    }

    private sealed class WebhookEndpointMarker;
}
