namespace ReviewBot.Llm.OpenAi;

internal static class OpenAiResponseFormats
{
    public const string JsonObject = "json_object";
    public const string JsonSchema = "json_schema";
    public const string Text = "text";

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"OpenAI response format must be one of: {JsonObject}, {JsonSchema}, {Text}.",
                nameof(value));
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            JsonObject or JsonSchema or Text => normalized,
            _ => throw new ArgumentException(
                $"OpenAI response format '{value}' is not supported. Accepted values: {JsonObject}, {JsonSchema}, {Text}.",
                nameof(value)),
        };
    }
}
