using System.Net;

namespace ReviewBot.GitHub.Auth;

public sealed class GitHubAuthException : Exception
{
    public GitHubAuthException(HttpStatusCode statusCode, string responseBody)
        : base($"GitHub authentication request failed with status {(int)statusCode} ({statusCode}): {responseBody}")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode StatusCode { get; }

    public string ResponseBody { get; }
}
