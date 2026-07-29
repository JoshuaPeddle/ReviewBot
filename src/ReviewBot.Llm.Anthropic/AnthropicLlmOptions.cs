namespace ReviewBot.Llm.Anthropic;

public sealed record AnthropicLlmOptions
{
    public const string SectionName = "Anthropic";

    public string ApiKey { get; set; } = string.Empty;

    public string ModelName { get; set; } = "claude-opus-4-7";

    public int MaxTokens { get; set; } = 4096;

    public decimal Temperature { get; set; } = 0.2m;

    public bool PromptCachingEnabled { get; set; } = true;

    public bool TokenCountingEnabled { get; set; } = true;

    public int TokenCountingHeuristicThresholdTokens { get; set; } = 8_000;

    /// <summary>
    /// How many chunk reviews to run against the API at once. Default 8.
    /// </summary>
    /// <remarks>
    /// Chunk review here used to be an unbounded <c>Task.WhenAll</c> over every chunk, so
    /// a large PR could open as many simultaneous connections as it had chunks — against a
    /// rate-limited API, with nothing holding it back. This makes the fan-out explicit and
    /// bounded. 8 sits above the chunk count of essentially every real review, so ordinary
    /// behaviour is unchanged while the tail case stops being unbounded.
    /// </remarks>
    public int MaxConcurrentRequests { get; set; } = 8;
}
