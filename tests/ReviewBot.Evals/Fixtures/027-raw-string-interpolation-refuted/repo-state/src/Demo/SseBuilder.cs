using System.Text.Json;

namespace Demo;

public static class SseBuilder
{
    public static string Chunk(string content)
    {
        var delta = $$"""
            {"id":"c1","choices":[{"delta":{"content":{{JsonSerializer.Serialize(content)}}}}]}
            """;

        return $"data: {delta}\n\n";
    }
}
