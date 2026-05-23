using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ReviewBot.GitHub.Auth;

namespace ReviewBot.GitHub.Tests.Auth;

public class GitHubAppJwtSignerTests
{
    [Fact]
    public void CreateAppJwtSignsVerifiableTokenWithPkcs8PrivateKey()
    {
        using var rsa = RSA.Create(2048);
        var signer = new GitHubAppJwtSigner(new GitHubAppOptions(
            AppId: 123456,
            PrivateKeyPem: rsa.ExportPkcs8PrivateKeyPem()));

        var now = new DateTimeOffset(2026, 5, 23, 12, 34, 56, TimeSpan.Zero);

        var jwt = signer.CreateAppJwt(now);

        var parts = jwt.Split('.');
        parts.Should().HaveCount(3);
        VerifySignature(rsa, parts).Should().BeTrue();

        using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
        header.RootElement.GetProperty("alg").GetString().Should().Be("RS256");
        header.RootElement.GetProperty("typ").GetString().Should().Be("JWT");

        using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        payload.RootElement.GetProperty("iss").GetString().Should().Be("123456");
        payload.RootElement.GetProperty("iat").GetInt64().Should().Be(now.AddSeconds(-60).ToUnixTimeSeconds());
        payload.RootElement.GetProperty("exp").GetInt64().Should().Be(now.AddMinutes(9).ToUnixTimeSeconds());
        var tokenLifetime = payload.RootElement.GetProperty("exp").GetInt64()
            - payload.RootElement.GetProperty("iat").GetInt64();
        tokenLifetime.Should().BeLessThanOrEqualTo((long)TimeSpan.FromMinutes(10).TotalSeconds);
    }

    [Fact]
    public void CreateAppJwtAcceptsPkcs1PrivateKeyPem()
    {
        using var rsa = RSA.Create(2048);
        var signer = new GitHubAppJwtSigner(new GitHubAppOptions(
            AppId: 789,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem()));

        var jwt = signer.CreateAppJwt(DateTimeOffset.UnixEpoch.AddHours(1));

        VerifySignature(rsa, jwt.Split('.')).Should().BeTrue();
    }

    private static bool VerifySignature(RSA rsa, IReadOnlyList<string> parts)
    {
        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        var signature = Base64UrlDecode(parts[2]);

        return rsa.VerifyData(
            signingInput,
            signature,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value
            .Replace('-', '+')
            .Replace('_', '/');

        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');

        return Convert.FromBase64String(padded);
    }
}
