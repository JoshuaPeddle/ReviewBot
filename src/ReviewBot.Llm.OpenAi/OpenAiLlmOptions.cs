namespace ReviewBot.Llm.OpenAi;

public sealed record OpenAiLlmOptions
{
    public string ApiKey { get; set; } = string.Empty;

    public string ModelName { get; set; } = "gpt-5.1";

    public Uri? BaseUrl { get; set; }

    public int MaxTokens { get; set; } = 4096;

    public float Temperature { get; set; } = 0.2f;

    public bool UseJsonMode { get; set; } = true;
}
