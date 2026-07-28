namespace ReviewBot.Persistence;

/// <summary>
/// Builds the primary key used to deduplicate webhook deliveries.
/// </summary>
internal static class DeliveryKeyBuilder
{
    public static string Build(string owner, string repo, string deliveryId) =>
        DeliveryKeyCanonicalizer.Canonicalize(owner, repo, deliveryId);
}
