namespace ReviewBot.Core.Llm;

public sealed class LlmResponseException : Exception
{
    private const int MaxMessageRawResponseLength = 2048;

    public LlmResponseException(string rawResponse, string? parseError)
        : base(BuildMessage(rawResponse, parseError))
    {
        RawResponse = rawResponse;
        ParseError = parseError;
    }

    public string RawResponse { get; }

    public string? ParseError { get; }

    private static string BuildMessage(string rawResponse, string? parseError)
    {
        var truncatedRawResponse = rawResponse.Length <= MaxMessageRawResponseLength
            ? rawResponse
            : rawResponse[..MaxMessageRawResponseLength];

        return $"LLM response was not valid review JSON. Parse error: {parseError ?? "unknown"}. Raw response (truncated to 2048 chars): {truncatedRawResponse}";
    }
}
