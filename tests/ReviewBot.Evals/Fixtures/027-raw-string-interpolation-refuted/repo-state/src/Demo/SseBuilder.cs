using System.Text.Json;

namespace Demo;

public static class SseBuilder
{
    public static string Chunk(string content)
    {
        var delta = $$"""
            {"content":{{JsonSerializer.Serialize(content)}},"done":false}
            """;

        return $"data: {delta}\n\n";
    }
}
