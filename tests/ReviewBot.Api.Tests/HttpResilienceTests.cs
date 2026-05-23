using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReviewBot.Api;
using ReviewBot.GitHub.Auth;

namespace ReviewBot.Api.Tests;

public sealed class HttpResilienceTests
{
    [Fact]
    public async Task InstallationTokenClientRetriesTransientServerErrorsThreeTimes()
    {
        using var rsa = RSA.Create(2048);
        var handler = new CountingHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new GitHubAppJwtSigner(new GitHubAppOptions(
            AppId: 123456,
            PrivateKeyPem: rsa.ExportPkcs8PrivateKeyPem())));
        services.AddHttpClient<InstallationTokenClient>()
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddReviewBotHttpResilience();

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<InstallationTokenClient>();

        var token = await client.GetTokenAsync(12345, CancellationToken.None);

        token.Token.Should().Be("ghs_installation_token");
        handler.Attempts.Should().Be(4);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts < 4)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("""{"message":"try again"}"""),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    """{"token":"ghs_installation_token","expires_at":"2026-05-23T13:45:00Z"}"""),
            });
        }
    }
}
