namespace ReviewBot.Core.Notifications;

public interface INotificationChannel
{
    Task SendAsync(ReviewNotification notification, CancellationToken ct);
}

public sealed record ReviewNotification(
    int PrNumber,
    string Summary,
    string Recipient,
    IReadOnlyList<string> Highlights);
