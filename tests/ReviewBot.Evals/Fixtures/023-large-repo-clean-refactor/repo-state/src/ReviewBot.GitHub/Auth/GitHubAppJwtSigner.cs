using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StringBuilder = System.Text.StringBuilder;

namespace ReviewBot.GitHub.Auth;

public sealed class GitHubAppJwtSigner
{
    private static readonly byte[] JwtHeaderBytes = Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}""");

    private readonly GitHubAppOptions options;
    private readonly Lazy<RSA> signingKey;
    private readonly object signingLock = new();

    public GitHubAppJwtSigner(GitHubAppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.AppId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.AppId, "GitHub App ID must be positive.");
        }

        if (string.IsNullOrWhiteSpace(options.PrivateKeyPem) &&
            string.IsNullOrWhiteSpace(options.PrivateKeyPemFile))
        {
            throw new ArgumentException("GitHub App private key PEM must be provided.", nameof(options));
        }

        this.options = options;
        signingKey = new Lazy<RSA>(
            () =>
            {
                var pem = string.IsNullOrWhiteSpace(options.PrivateKeyPem)
                    ? File.ReadAllText(options.PrivateKeyPemFile)
                    : options.PrivateKeyPem;
                return LoadPrivateKey(pem);
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string CreateAppJwt(DateTimeOffset now)
    {
        var issuedAt = now.AddSeconds(-60).ToUnixTimeSeconds();
        var expiresAt = now.AddMinutes(9).ToUnixTimeSeconds();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new JwtPayload(
            IssuedAt: issuedAt,
            ExpiresAt: expiresAt,
            Issuer: options.AppId.ToString()));

        var header = Base64UrlEncode(JwtHeaderBytes);
        var claims = Base64UrlEncode(payload);
        var signingInput = $"{header}.{claims}";
        var signingInputBytes = Encoding.ASCII.GetBytes(signingInput);
        byte[] signature;

        lock (signingLock)
        {
            signature = signingKey.Value.SignData(
                signingInputBytes,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }

        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static RSA LoadPrivateKey(string privateKeyPem)
    {
        var rsa = RSA.Create();

        try
        {
            rsa.ImportFromPem(privateKeyPem);
            return rsa;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }
    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert
            .ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed record JwtPayload(
        [property: JsonPropertyName("iat")] long IssuedAt,
        [property: JsonPropertyName("exp")] long ExpiresAt,
        [property: JsonPropertyName("iss")] string Issuer);
}
