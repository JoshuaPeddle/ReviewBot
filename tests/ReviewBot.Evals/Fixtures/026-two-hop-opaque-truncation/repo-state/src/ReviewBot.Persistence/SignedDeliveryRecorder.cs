namespace ReviewBot.Persistence;

/// <summary>
/// Records a delivery together with the signature it arrived with, so a replay of the
/// same delivery id under a different signature is not mistaken for a genuine retry.
/// </summary>
internal sealed class SignedDeliveryRecorder
{
    /// <summary>
    /// Builds the storage key identifying a delivery and the signature it carried.
    /// </summary>
    public static string BuildSignedKey(string owner, string repo, string deliveryId, string signatureHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signatureHex);

        var deliveryKey = DeliveryKeyBuilder.Build(owner, repo, deliveryId);

        return $"{deliveryKey}:{signatureHex}";
    }
}
