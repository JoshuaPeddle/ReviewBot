namespace ReviewBot.Llm.OpenAi;

/// <summary>
/// Thrown when an OpenAI-compatible server rejects a chat completion with a
/// non-success status. Unlike the raw SDK exception, this carries the response
/// body so the worker logs the server's actual error (e.g. a context-length
/// message) instead of a bare status code.
/// </summary>
internal sealed class OpenAiChatRequestException : Exception
{
    public OpenAiChatRequestException(
        int status,
        string? responseBody,
        Exception innerException,
        Uri? baseUrl = null,
        string? modelName = null)
        : base(BuildMessage(status, responseBody, baseUrl, modelName), innerException)
    {
        Status = status;
        ResponseBody = responseBody;
    }

    public int Status { get; }

    public string? ResponseBody { get; }

    private static string BuildMessage(int status, string? responseBody, Uri? baseUrl, string? modelName)
    {
        var message = string.IsNullOrWhiteSpace(responseBody)
            ? $"OpenAI-compatible request failed with status {status}."
            : $"OpenAI-compatible request failed with status {status}: {responseBody}";

        // A 404 or 401 from an OpenAI-compatible server nearly always means the endpoint
        // or model is wrong, or the key is — not that the request was malformed. The raw
        // SDK exception names none of those, which turns a stale base URL into an opaque
        // "Service request failed" and a dead review job. Say what we actually called.
        var hint = status switch
        {
            404 => "The endpoint or model was not found. Check REVIEWBOT__OpenAi__BaseUrl "
                   + "(it must include the API suffix, e.g. /v1) and REVIEWBOT__OpenAi__ModelName "
                   + "against the server's /models listing.",
            401 or 403 => "The server rejected the credentials. Check REVIEWBOT__OpenAi__ApiKey.",
            _ => null,
        };

        if (hint is null)
        {
            return message;
        }

        var target = (baseUrl, modelName) switch
        {
            (not null, not null) => $" Called base URL '{baseUrl}' with model '{modelName}'.",
            (not null, null) => $" Called base URL '{baseUrl}'.",
            (null, not null) => $" Called model '{modelName}'.",
            _ => string.Empty,
        };

        return $"{message}{target} {hint}";
    }
}
