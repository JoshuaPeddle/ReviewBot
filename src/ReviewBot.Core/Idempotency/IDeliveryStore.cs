namespace ReviewBot.Core.Idempotency;

public interface IDeliveryStore
{
    Task<bool> TryRecordAsync(string deliveryId, CancellationToken ct);

    Task CleanupAsync(DateTimeOffset olderThan, CancellationToken ct);
}
