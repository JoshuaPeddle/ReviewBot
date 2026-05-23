using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReviewBot.Api.Webhooks;
using ReviewBot.Core.Idempotency;
using ReviewBot.Core.Jobs;

namespace ReviewBot.Api.Tests.Webhooks;

public class WebhookEndpointTests
{
    private const string Secret = "test-webhook-secret";
    private const string BotSlug = "reviewbot[bot]";

    [Fact]
    public async Task BadSignatureReturnsUnauthorized()
    {
        using var queue = new CapturingReviewJobQueue();
        await using var factory = CreateFactory(queue);
        using var client = factory.CreateClient();
        var payload = CreatePullRequestPayload("review_requested", BotSlug);
        using var request = CreateWebhookRequest(payload, "sha256=bad");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        queue.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task WrongEventTypeReturnsNoContent()
    {
        using var queue = new CapturingReviewJobQueue();
        await using var factory = CreateFactory(queue);
        using var client = factory.CreateClient();
        var payload = CreatePullRequestPayload("review_requested", BotSlug);
        using var request = CreateWebhookRequest(payload, eventName: "push");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        queue.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewRequestedForConfiguredBotReturnsAcceptedAndEnqueuesJob()
    {
        using var queue = new CapturingReviewJobQueue();
        var store = new CapturingDeliveryStore();
        await using var factory = CreateFactory(queue, store);
        using var client = factory.CreateClient();
        var payload = CreatePullRequestPayload("review_requested", BotSlug);
        using var request = CreateWebhookRequest(payload);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        queue.Jobs.Should().ContainSingle().Which.Should().Be(new ReviewJob(
            DeliveryId: "delivery-123",
            InstallationId: 98765,
            Owner: "octo-org",
            Repo: "reviewbot",
            PrNumber: 42,
            HeadSha: "head-sha-abc",
            Reason: "review_requested"));
        store.RecordedDeliveryIds.Should().Equal("delivery-123");
    }

    [Fact]
    public async Task ReviewRequestedForDifferentReviewerReturnsNoContent()
    {
        using var queue = new CapturingReviewJobQueue();
        await using var factory = CreateFactory(queue);
        using var client = factory.CreateClient();
        var payload = CreatePullRequestPayload("review_requested", "someone-else");
        using var request = CreateWebhookRequest(payload);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        queue.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task SynchronizeReturnsAcceptedAndEnqueuesJob()
    {
        using var queue = new CapturingReviewJobQueue();
        await using var factory = CreateFactory(queue);
        using var client = factory.CreateClient();
        var payload = CreatePullRequestPayload("synchronize", requestedReviewer: null);
        using var request = CreateWebhookRequest(payload);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        queue.Jobs.Should().ContainSingle().Which.Reason.Should().Be("synchronize");
    }

    [Fact]
    public async Task DuplicateAcceptedDeliveryReturnsOkWithoutEnqueueingJob()
    {
        using var queue = new CapturingReviewJobQueue();
        var store = new CapturingDeliveryStore(recordAsNew: false);
        await using var factory = CreateFactory(queue, store);
        using var client = factory.CreateClient();
        var payload = CreatePullRequestPayload("review_requested", BotSlug);
        using var request = CreateWebhookRequest(payload);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        queue.Jobs.Should().BeEmpty();
        store.RecordedDeliveryIds.Should().Equal("delivery-123");
    }

    private static WebApplicationFactory<Program> CreateFactory(
        CapturingReviewJobQueue queue,
        IDeliveryStore? deliveryStore = null)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IReviewJobQueue>();
                    services.RemoveAll<ChannelReviewJobQueue>();
                    services.RemoveAll<IDeliveryStore>();
                    services.AddSingleton<IReviewJobQueue>(queue);
                    services.AddSingleton(deliveryStore ?? new CapturingDeliveryStore());
                    services.Configure<WebhookOptions>(options =>
                    {
                        options.Secret = Secret;
                        options.BotSlug = BotSlug;
                    });
                });
            });
    }

    private static HttpRequestMessage CreateWebhookRequest(
        string payload,
        string? signature = null,
        string eventName = "pull_request")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhook")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-GitHub-Delivery", "delivery-123");
        request.Headers.Add("X-GitHub-Event", eventName);
        request.Headers.Add("X-Hub-Signature-256", signature ?? Sign(payload));

        return request;
    }

    private static string Sign(string payload)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(payload));
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static string CreatePullRequestPayload(string action, string? requestedReviewer)
    {
        var requestedReviewerJson = requestedReviewer is null
            ? "null"
            : $$"""{"login":"{{requestedReviewer}}"}""";

        return $$"""
            {
              "action": "{{action}}",
              "installation": {
                "id": 98765
              },
              "repository": {
                "name": "reviewbot",
                "owner": {
                  "login": "octo-org"
                }
              },
              "pull_request": {
                "number": 42,
                "html_url": "https://github.com/octo-org/reviewbot/pull/42",
                "head": {
                  "sha": "head-sha-abc"
                },
                "user": {
                  "login": "developer"
                },
                "requested_reviewers": [
                  {
                    "login": "reviewbot[bot]"
                  }
                ]
              },
              "requested_reviewer": {{requestedReviewerJson}},
              "sender": {
                "login": "developer"
              }
            }
            """;
    }

    private sealed class CapturingReviewJobQueue : IReviewJobQueue, IDisposable
    {
        private readonly List<ReviewJob> jobs = [];

        public IReadOnlyList<ReviewJob> Jobs => jobs;

        public ValueTask EnqueueAsync(ReviewJob job, CancellationToken ct)
        {
            jobs.Add(job);
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<ReviewJob> DequeueAllAsync(CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public void Dispose()
        {
            jobs.Clear();
        }
    }

    private sealed class CapturingDeliveryStore(bool recordAsNew = true) : IDeliveryStore
    {
        private readonly List<string> recordedDeliveryIds = [];

        public IReadOnlyList<string> RecordedDeliveryIds => recordedDeliveryIds;

        public Task<bool> TryRecordAsync(string deliveryId, CancellationToken ct)
        {
            recordedDeliveryIds.Add(deliveryId);
            return Task.FromResult(recordAsNew);
        }

        public Task CleanupAsync(DateTimeOffset olderThan, CancellationToken ct)
        {
            throw new NotSupportedException();
        }
    }
}
