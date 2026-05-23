using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewBot.GitHub.Auth;

namespace ReviewBot.GitHub.Tests.Auth;

public class InstallationTokenClientTests
{
    [Fact]
    public async Task GetTokenAsyncPostsToGitHubWithExpectedHeadersAndParsesResponse()
    {
        using var rsa = RSA.Create(2048);
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(
                """{"token":"ghs_installation_token","expires_at":"2026-05-23T13:45:00Z"}"""),
        });
        var client = CreateClient(handler, rsa);

        var token = await client.GetTokenAsync(12345, CancellationToken.None);

        token.Should().Be(new InstallationToken(
            "ghs_installation_token",
            new DateTimeOffset(2026, 5, 23, 13, 45, 0, TimeSpan.Zero)));

        handler.Requests.Should().ContainSingle();
        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri.Should().Be("https://api.github.com/app/installations/12345/access_tokens");
        request.Headers.Authorization.Should().NotBeNull();
        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.Authorization.Parameter.Should().NotBeNullOrWhiteSpace();
        request.Headers.Accept.Select(value => value.MediaType).Should().Contain("application/vnd.github+json");
        request.Headers.UserAgent.ToString().Should().Contain("ReviewBot");
    }

    [Fact]
    public async Task GetTokenAsyncThrowsGitHubAuthExceptionOnNonSuccess()
    {
        using var rsa = RSA.Create(2048);
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"message":"Bad credentials"}"""),
        });
        var client = CreateClient(handler, rsa);

        var act = () => client.GetTokenAsync(99, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<GitHubAuthException>();
        exception.Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        exception.Which.ResponseBody.Should().Contain("Bad credentials");
    }

    private static InstallationTokenClient CreateClient(CapturingHandler handler, RSA rsa) =>
        new(
            new HttpClient(handler),
            new GitHubAppJwtSigner(new GitHubAppOptions(123456, rsa.ExportPkcs8PrivateKeyPem())),
            NullLogger<InstallationTokenClient>.Instance,
            new ManualTimeProvider(new DateTimeOffset(2026, 5, 23, 12, 0, 0, TimeSpan.Zero)));

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(CloneRequest(request));
            return Task.FromResult(responder(request));
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);

            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }
    }
}
