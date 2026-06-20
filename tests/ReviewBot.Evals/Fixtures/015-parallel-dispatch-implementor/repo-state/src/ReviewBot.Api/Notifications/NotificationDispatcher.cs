using ReviewBot.Core.Notifications;

namespace ReviewBot.Api.Notifications;

public sealed class NotificationDispatcher
{
    private readonly IReadOnlyList<INotificationChannel> channels;

    public NotificationDispatcher(IReadOnlyList<INotificationChannel> channels)
    {
        this.channels = channels;
    }

    public async Task DispatchAsync(ReviewNotification notification, CancellationToken ct)
    {
        var sends = channels.Select(channel => channel.SendAsync(notification, ct));
        await Task.WhenAll(sends).ConfigureAwait(false);
    }
}
