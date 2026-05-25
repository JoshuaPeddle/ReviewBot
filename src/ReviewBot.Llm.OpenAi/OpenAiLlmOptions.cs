namespace ReviewBot.Llm.OpenAi;

public sealed record OpenAiLlmOptions
{
    public const string SectionName = "OpenAi";

    public string ApiKey { get; set; } = string.Empty;

    public string ModelName { get; set; } = "gpt-5.1";

    public Uri? BaseUrl { get; set; }

    public int MaxTokens { get; set; } = 4096;

    public float Temperature { get; set; } = 0.2f;

    private string responseFormat = OpenAiResponseFormats.JsonObject;

    public string ResponseFormat
    {
        get => responseFormat;
        set => responseFormat = OpenAiResponseFormats.Normalize(value);
    }

    public int TimeoutSeconds { get; set; } = 600;
}
