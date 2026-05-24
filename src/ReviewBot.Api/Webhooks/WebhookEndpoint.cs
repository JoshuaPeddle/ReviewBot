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
        return eventName switch
        {
            "pull_request" => await HandlePullRequestAsync(body, deliveryId, queue, deliveryStore, logger, ct),
            "issue_comment" => await HandleIssueCommentAsync(body, deliveryId, queue, deliveryStore, logger, ct),
            _ => Results.NoContent()
        };
    }

    private static async Task<IResult> HandlePullRequestAsync(
        byte[] body,
        string deliveryId,
        IReviewJobQueue queue,
        IDeliveryStore deliveryStore,
        ILogger<WebhookEndpointMarker> logger,
        CancellationToken ct)
    {
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

    private static async Task<IResult> HandleIssueCommentAsync(
        byte[] body,
        string deliveryId,
        IReviewJobQueue queue,
        IDeliveryStore deliveryStore,
        ILogger<WebhookEndpointMarker> logger,
        CancellationToken ct)
    {
        IssueCommentEvent? payload;
        try
        {
            payload = JsonSerializer.Deserialize<IssueCommentEvent>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Rejected issue_comment delivery {DeliveryId}: malformed JSON", deliveryId);
            return Results.BadRequest();
        }

        if (payload is null || !HasRequiredCommentFields(payload))
        {
            logger.LogWarning("Rejected issue_comment delivery {DeliveryId}: missing required fields", deliveryId);
            return Results.BadRequest();
        }

        if (!ShouldEnqueueComment(payload))
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
            PrNumber: payload.Issue.Number,
            HeadSha: null,
            Reason: "review_comment");

        await queue.EnqueueAsync(job, ct);
        logger.LogInformation(
            "Accepted webhook delivery {DeliveryId} for {Owner}/{Repo}#{PrNumber} because of review_comment",
            job.DeliveryId,
            job.Owner,
            job.Repo,
            job.PrNumber);

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

    private static bool ShouldEnqueueComment(IssueCommentEvent payload) =>
        string.Equals(payload.Action, "created", StringComparison.Ordinal) &&
        payload.Issue.PullRequest is not null &&
        string.Equals(payload.Comment.Body.Trim(), "/review", StringComparison.OrdinalIgnoreCase);

    private static bool HasRequiredFields(PullRequestEvent payload) =>
        payload.Installation.Id > 0 &&
        payload.PullRequest.Number > 0 &&
        !string.IsNullOrWhiteSpace(payload.PullRequest.Head.Sha) &&
        !string.IsNullOrWhiteSpace(payload.Repository.Name) &&
        !string.IsNullOrWhiteSpace(payload.Repository.Owner.Login);

    private static bool HasRequiredCommentFields(IssueCommentEvent payload) =>
        payload.Installation.Id > 0 &&
        payload.Issue.Number > 0 &&
        !string.IsNullOrWhiteSpace(payload.Repository.Name) &&
        !string.IsNullOrWhiteSpace(payload.Repository.Owner.Login);

    private sealed class WebhookEndpointMarker;
}
