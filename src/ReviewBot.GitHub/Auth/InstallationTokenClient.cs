using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ReviewBot.GitHub.Auth;

public sealed class InstallationTokenClient : IInstallationTokenProvider
{
    private const string GitHubApiBaseUrl = "https://api.github.com";

    private readonly HttpClient httpClient;
    private readonly GitHubAppJwtSigner jwtSigner;
    private readonly ILogger<InstallationTokenClient> logger;
    private readonly TimeProvider clock;

    public InstallationTokenClient(
        HttpClient httpClient,
        GitHubAppJwtSigner jwtSigner,
        ILogger<InstallationTokenClient> logger,
        TimeProvider? clock = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.jwtSigner = jwtSigner ?? throw new ArgumentNullException(nameof(jwtSigner));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.clock = clock ?? TimeProvider.System;
    }

    public async Task<InstallationToken> GetTokenAsync(long installationId, CancellationToken ct)
    {
        if (installationId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(installationId), installationId, "Installation ID must be positive.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{GitHubApiBaseUrl}/app/installations/{installationId}/access_tokens");

        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            jwtSigner.CreateAppJwt(clock.GetUtcNow()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("ReviewBot");

        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "GitHub installation token request failed with status {StatusCode} for installation {InstallationId}",
                response.StatusCode,
                installationId);
            throw new GitHubAuthException(response.StatusCode, responseBody);
        }

        return ParseTokenResponse(responseBody);
    }

    private static InstallationToken ParseTokenResponse(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;

        var token = root.GetProperty("token").GetString();
        var expiresAtText = root.GetProperty("expires_at").GetString();

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(expiresAtText))
        {
            throw new JsonException("GitHub installation token response is missing token or expires_at.");
        }

        var expiresAt = DateTimeOffset.Parse(
            expiresAtText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        return new InstallationToken(token, expiresAt);
    }
}
