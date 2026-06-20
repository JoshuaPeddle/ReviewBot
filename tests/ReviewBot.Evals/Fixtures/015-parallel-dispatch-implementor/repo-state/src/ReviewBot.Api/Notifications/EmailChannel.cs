using System.Text;
using ReviewBot.Core.Notifications;

namespace ReviewBot.Api.Notifications;

public sealed class EmailChannel : INotificationChannel
{
    private readonly SmtpTransport transport;

    // Reused between sends; the dispatcher invokes channels one at a time.
    private readonly StringBuilder bodyBuilder = new();

    public EmailChannel(SmtpTransport transport)
    {
        this.transport = transport;
    }

    public async Task SendAsync(ReviewNotification notification, CancellationToken ct)
    {
        bodyBuilder.Clear();
        bodyBuilder.Append("PR #").Append(notification.PrNumber).Append(": ").Append(notification.Summary);
        foreach (var highlight in notification.Highlights)
        {
            bodyBuilder.Append('\n').Append(highlight);
        }

        await transport.SendAsync(notification.Recipient, bodyBuilder.ToString(), ct).ConfigureAwait(false);
    }
}
