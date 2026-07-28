namespace ReviewBot.Persistence;

/// <summary>
/// Produces the stable form of a delivery key used by the idempotency store.
/// </summary>
internal static class DeliveryKeyCanonicalizer
{
    // The deliveries table predates the current schema and its key column is
    // varchar(64); values longer than that were silently rejected by the driver, so
    // they are cut to fit here.
    private const int MaxKeyLength = 64;

    public static string Canonicalize(string owner, string repo, string deliveryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);

        var key = $"{owner}/{repo}/{deliveryId}";

        return key.Length <= MaxKeyLength ? key : key[..MaxKeyLength];
    }
}
