using System.Text.Json;
using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Llm;

public static class SelfCritiqueParser
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static SelfCritiqueResult? Parse(string rawResponse, int proposedCommentCount)
    {
        ArgumentNullException.ThrowIfNull(rawResponse);

        if (proposedCommentCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(proposedCommentCount));
        }

        var candidate = StripLeadingFence(rawResponse.Trim());
        var bounds = FindJsonObjectBounds(candidate);
        if (bounds is null)
        {
            return null;
        }

        var json = candidate.Substring(bounds.Value.Start, bounds.Value.Length);

        try
        {
            using var document = JsonDocument.Parse(json, JsonOptions);
            return ParseRoot(document.RootElement, proposedCommentCount);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SelfCritiqueResult? ParseRoot(JsonElement root, int proposedCommentCount)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!TryGetProperty(root, "retained_indices", out var retainedIndicesElement) ||
            retainedIndicesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var retainedIndices = new List<int>();
        var seenIndices = new HashSet<int>();
        foreach (var indexElement in retainedIndicesElement.EnumerateArray())
        {
            if (indexElement.ValueKind != JsonValueKind.Number ||
                !indexElement.TryGetInt32(out var index) ||
                index < 0 ||
                index >= proposedCommentCount ||
                !seenIndices.Add(index))
            {
                return null;
            }

            retainedIndices.Add(index);
        }

        var rationale = TryGetString(root, "rationale", out var value)
            ? value
            : string.Empty;

        return new SelfCritiqueResult(retainedIndices, rationale);
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (TryGetProperty(element, name, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement property)
    {
        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static string StripLeadingFence(string value)
    {
        if (!value.StartsWith("```", StringComparison.Ordinal))
        {
            return value;
        }

        var firstLineEnd = value.IndexOf('\n', StringComparison.Ordinal);
        if (firstLineEnd < 0)
        {
            return value;
        }

        var withoutOpeningFence = value[(firstLineEnd + 1)..];
        var closingFenceIndex = withoutOpeningFence.LastIndexOf("```", StringComparison.Ordinal);
        return closingFenceIndex < 0
            ? withoutOpeningFence.Trim()
            : withoutOpeningFence[..closingFenceIndex].Trim();
    }

    private static (int Start, int Length)? FindJsonObjectBounds(string value)
    {
        var start = value.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var isEscaped = false;

        for (var index = start; index < value.Length; index++)
        {
            var current = value[index];

            if (isEscaped)
            {
                isEscaped = false;
                continue;
            }

            if (current == '\\' && inString)
            {
                isEscaped = true;
                continue;
            }

            if (current == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (current == '{')
            {
                depth++;
                continue;
            }

            if (current != '}')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return (start, index - start + 1);
            }
        }

        return null;
    }
}
