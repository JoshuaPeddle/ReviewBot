namespace ReviewBot.Llm.OpenAi;

/// <summary>
/// Thrown when an OpenAI-compatible server rejects a chat completion with a
/// non-success status. Unlike the raw SDK exception, this carries the response
/// body so the worker logs the server's actual error (e.g. a context-length
/// message) instead of a bare status code.
/// </summary>
internal sealed class OpenAiChatRequestException : Exception
{
    public OpenAiChatRequestException(int status, string? responseBody, Exception innerException)
        : base(BuildMessage(status, responseBody), innerException)
    {
        Status = status;
        ResponseBody = responseBody;
    }

    public int Status { get; }

    public string? ResponseBody { get; }

    private static string BuildMessage(int status, string? responseBody) =>
        string.IsNullOrWhiteSpace(responseBody)
            ? $"OpenAI-compatible request failed with status {status}."
            : $"OpenAI-compatible request failed with status {status}: {responseBody}";
}
