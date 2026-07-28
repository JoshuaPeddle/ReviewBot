namespace ReviewBot.Llm.OpenAi;

public sealed record OpenAiLlmOptions
{
    public const string SectionName = "OpenAi";

    public string ApiKey { get; set; } = string.Empty;

    public string ModelName { get; set; } = "gpt-5.1";

    public Uri? BaseUrl { get; set; }

    public int MaxTokens { get; set; } = 4096;

    public float Temperature { get; set; } = 0.2f;

    /// <summary>
    /// Optional sampling knobs beyond temperature. Null (the default) sends none of
    /// them and leaves the server's defaults in place.
    /// </summary>
    public OpenAiSamplingOptions? Sampling { get; set; }

    private string responseFormat = OpenAiResponseFormats.Text;

    public string ResponseFormat
    {
        get => responseFormat;
        set => responseFormat = OpenAiResponseFormats.Normalize(value);
    }

    public int TimeoutSeconds { get; set; } = 600;
}
