using System.Security.Cryptography;
using System.Text;

namespace ReviewBot.E2E.Tests.Infrastructure;

public sealed class WebhookSender(HttpClient client, string secret)
{
    public async Task<HttpResponseMessage> SendPullRequestAsync(
        string payload,
        string? deliveryId = null,
        CancellationToken ct = default)
    {
        using var request = CreatePullRequestRequest(payload, secret, deliveryId);
        return await client.SendAsync(request, ct);
    }

    public static HttpRequestMessage CreatePullRequestRequest(
        string payload,
        string secret,
        string? deliveryId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var request = new HttpRequestMessage(HttpMethod.Post, "/webhook")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-GitHub-Delivery", deliveryId ?? $"delivery-{Guid.NewGuid():N}");
        request.Headers.Add("X-GitHub-Event", "pull_request");
        request.Headers.Add("X-Hub-Signature-256", Sign(payload, secret));
        return request;
    }

    public static string Sign(string payload, string secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
