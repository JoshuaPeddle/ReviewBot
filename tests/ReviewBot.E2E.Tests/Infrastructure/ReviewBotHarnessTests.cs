using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;

namespace ReviewBot.E2E.Tests.Infrastructure;

[Collection(E2eCollection.Name)]
public sealed class ReviewBotHarnessTests(ReviewBotHarness harness)
{
    [Fact]
    public async Task HealthzReturnsOk()
    {
        await harness.ResetAsync();
        using var client = harness.CreateClient();

        using var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ResetAsyncClearsWireMockMappingsAndLogs()
    {
        await harness.ResetAsync();
        using var client = new HttpClient { BaseAddress = new Uri(harness.GitHubMock.Url!) };
        using var response = await client.GetAsync("/anything");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        harness.GitHubMock.LogEntries.Should().NotBeEmpty();

        await harness.ResetAsync();

        harness.GitHubMock.LogEntries.Should().BeEmpty();
    }

    [Fact]
    public void WebhookSenderCreatesSignedPullRequestWebhook()
    {
        const string payload = """{"zen":"keep it logically tidy"}""";

        using var request = WebhookSender.CreatePullRequestRequest(
            payload,
            ReviewBotHarness.WebhookSecret,
            deliveryId: "delivery-123");

        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri.Should().Be("/webhook");
        request.Headers.GetValues("X-GitHub-Delivery").Should().Equal("delivery-123");
        request.Headers.GetValues("X-GitHub-Event").Should().Equal("pull_request");
        request.Headers.GetValues("X-Hub-Signature-256").Should().Equal(ExpectedSignature(payload));
    }

    [Fact]
    public async Task WorkerSyncHelperWaitsForMatchingRequest()
    {
        await harness.ResetAsync();
        using var client = new HttpClient { BaseAddress = new Uri(harness.GitHubMock.Url!) };

        using var response = await client.PostAsync("/repos/owner/repo/pulls/1/reviews", new StringContent("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await WorkerSyncHelper.WaitForRequestAsync(
            harness.GitHubMock,
            path => path == "/repos/owner/repo/pulls/1/reviews");
    }

    [Fact]
    public async Task WorkerSyncHelperWaitForNoCallPassesWhenRequestIsAbsent()
    {
        await harness.ResetAsync();

        await WorkerSyncHelper.WaitForNoCallAsync(
            harness.GitHubMock,
            path => path == "/repos/owner/repo/pulls/1/reviews",
            quietPeriod: TimeSpan.FromMilliseconds(100));
    }

    private static string ExpectedSignature(string payload)
    {
        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(ReviewBotHarness.WebhookSecret),
            Encoding.UTF8.GetBytes(payload));
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
