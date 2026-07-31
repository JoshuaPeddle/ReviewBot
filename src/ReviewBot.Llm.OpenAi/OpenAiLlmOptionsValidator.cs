namespace ReviewBot.Llm.OpenAi;

internal static class OpenAiLlmOptionsValidator
{
    private const int MaxOutputTokens = 1_000_000;
    private const int MaxTimeoutSeconds = 3_600;
    private const int MaxConcurrency = 64;
    private const int MaxTopK = 1_000_000;

    public static void Validate(OpenAiLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ModelName))
        {
            throw new InvalidOperationException("OpenAi:ModelName must be provided.");
        }

        if (!string.Equals(options.ModelName, options.ModelName.Trim(), StringComparison.Ordinal) ||
            options.ModelName.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "OpenAi:ModelName must not have leading or trailing whitespace or contain control characters.");
        }

        if (options.BaseUrl is { } baseUrl &&
            (!baseUrl.IsAbsoluteUri ||
             string.IsNullOrWhiteSpace(baseUrl.Host) ||
             (!string.Equals(baseUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
              !string.Equals(baseUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException("OpenAi:BaseUrl must be an absolute HTTP(S) URI.");
        }

        ValidateIntegerRange("OpenAi:MaxTokens", options.MaxTokens, 1, MaxOutputTokens);
        ValidateFloatRange("OpenAi:Temperature", options.Temperature, 0, 2);
        ValidateIntegerRange("OpenAi:TimeoutSeconds", options.TimeoutSeconds, 1, MaxTimeoutSeconds);
        ValidateIntegerRange("OpenAi:MaxConcurrentRequests", options.MaxConcurrentRequests, 1, MaxConcurrency);

        if (options.Sampling is not { } sampling)
        {
            return;
        }

        ValidateOptionalFloatRange("OpenAi:Sampling:TopP", sampling.TopP, 0, 1);
        if (sampling.TopK is { } topK)
        {
            ValidateIntegerRange("OpenAi:Sampling:TopK", topK, 1, MaxTopK);
        }

        ValidateOptionalFloatRange("OpenAi:Sampling:MinP", sampling.MinP, 0, 1);
        ValidateOptionalFloatRange("OpenAi:Sampling:PresencePenalty", sampling.PresencePenalty, -2, 2);
        if (sampling.RepetitionPenalty is { } repetitionPenalty &&
            (!float.IsFinite(repetitionPenalty) || repetitionPenalty <= 0 || repetitionPenalty > 2))
        {
            throw new InvalidOperationException(
                "OpenAi:Sampling:RepetitionPenalty must be finite, greater than 0, and at most 2.");
        }
    }

    private static void ValidateIntegerRange(string key, int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidOperationException($"{key} must be between {minimum} and {maximum}.");
        }
    }

    private static void ValidateOptionalFloatRange(string key, float? value, float minimum, float maximum)
    {
        if (value is { } configured)
        {
            ValidateFloatRange(key, configured, minimum, maximum);
        }
    }

    private static void ValidateFloatRange(string key, float value, float minimum, float maximum)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new InvalidOperationException($"{key} must be a finite value between {minimum} and {maximum}.");
        }
    }
}
