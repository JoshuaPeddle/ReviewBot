namespace ReviewBot.Llm.Anthropic;

public sealed record AnthropicLlmOptions
{
    public const string SectionName = "Anthropic";

    public string ApiKey { get; set; } = string.Empty;

    public string ModelName { get; set; } = "claude-opus-4-7";

    public int MaxTokens { get; set; } = 4096;

    public decimal Temperature { get; set; } = 0.2m;

    public bool PromptCachingEnabled { get; set; } = true;
}
