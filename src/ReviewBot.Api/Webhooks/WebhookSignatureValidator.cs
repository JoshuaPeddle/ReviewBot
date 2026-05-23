using System.Security.Cryptography;
using System.Text;

namespace ReviewBot.Api.Webhooks;

public static class WebhookSignatureValidator
{
    private const string SignaturePrefix = "sha256=";
    private const int Sha256ByteLength = 32;
    private const int Sha256HexLength = Sha256ByteLength * 2;

    public static bool IsValid(string secret, ReadOnlySpan<byte> body, string? signatureHeader)
    {
        ArgumentNullException.ThrowIfNull(secret);

        if (string.IsNullOrWhiteSpace(signatureHeader) ||
            !signatureHeader.StartsWith(SignaturePrefix, StringComparison.Ordinal) ||
            signatureHeader.Length != SignaturePrefix.Length + Sha256HexLength)
        {
            return false;
        }

        Span<byte> expectedSignature = stackalloc byte[Sha256ByteLength];
        var hex = signatureHeader.AsSpan(SignaturePrefix.Length);
        if (!TryDecodeHex(hex, expectedSignature))
        {
            return false;
        }

        var secretBytes = Encoding.UTF8.GetBytes(secret);
        Span<byte> actualSignature = stackalloc byte[Sha256ByteLength];
        int bytesWritten;
        if (!HMACSHA256.TryHashData(secretBytes, body, actualSignature, out bytesWritten) ||
            bytesWritten != Sha256ByteLength)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(actualSignature, expectedSignature);
    }

    private static bool TryDecodeHex(ReadOnlySpan<char> hex, Span<byte> destination)
    {
        if (hex.Length != destination.Length * 2)
        {
            return false;
        }

        for (var index = 0; index < destination.Length; index++)
        {
            var high = FromHexDigit(hex[index * 2]);
            var low = FromHexDigit(hex[(index * 2) + 1]);
            if (high < 0 || low < 0)
            {
                return false;
            }

            destination[index] = (byte)((high << 4) | low);
        }

        return true;
    }

    private static int FromHexDigit(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1
    };
}
