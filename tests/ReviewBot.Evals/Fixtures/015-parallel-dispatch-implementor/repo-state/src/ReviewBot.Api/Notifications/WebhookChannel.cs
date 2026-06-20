using System.Net.Http.Json;
using ReviewBot.Core.Notifications;

namespace ReviewBot.Api.Notifications;

public sealed class WebhookChannel : INotificationChannel
{
    private readonly HttpClient http;

    public WebhookChannel(HttpClient http)
    {
        this.http = http;
    }

    public async Task SendAsync(ReviewNotification notification, CancellationToken ct)
    {
        var payload = new
        {
            pr = notification.PrNumber,
            summary = notification.Summary,
            highlights = notification.Highlights
        };
        using var response = await http.PostAsJsonAsync("/hooks/review-complete", payload, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
