using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Llm;

public static class LlmResultParser
{
    private const int MaxComments = 100;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static ParseResult Parse(string rawResponse, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(rawResponse);

        var candidate = StripLeadingFence(rawResponse.Trim());
        var bounds = FindJsonObjectBounds(candidate);
        if (bounds is null)
        {
            return new ParseResult(Success: false, Value: null, Error: "Response did not contain a JSON object.");
        }

        var json = candidate.Substring(bounds.Value.Start, bounds.Value.Length);

        try
        {
            using var document = JsonDocument.Parse(json, JsonOptions);
            return ParseRoot(document.RootElement, logger);
        }
        catch (JsonException exception)
        {
            return new ParseResult(Success: false, Value: null, Error: $"Response JSON could not be parsed: {exception.Message}");
        }
    }

    private static ParseResult ParseRoot(JsonElement root, ILogger? logger)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ParseResult(Success: false, Value: null, Error: "Response JSON root must be an object.");
        }

        if (!TryGetProperty(root, "summary", out var summaryElement) ||
            summaryElement.ValueKind != JsonValueKind.String)
        {
            return new ParseResult(Success: false, Value: null, Error: "Response JSON must include a string summary.");
        }

        if (!TryGetProperty(root, "comments", out var commentsElement) ||
            commentsElement.ValueKind != JsonValueKind.Array)
        {
            return new ParseResult(Success: false, Value: null, Error: "Response JSON must include a comments array.");
        }

        var summary = summaryElement.GetString()!;
        var comments = new List<InlineComment>();

        foreach (var commentElement in commentsElement.EnumerateArray())
        {
            if (comments.Count >= MaxComments)
            {
                break;
            }

            if (TryParseComment(commentElement, out var comment, out var error))
            {
                comments.Add(comment);
                continue;
            }

            logger?.LogWarning("Dropped invalid LLM review comment: {Reason}", error);
        }

        return new ParseResult(
            Success: true,
            Value: new ReviewResult(summary, comments),
            Error: null);
    }

    private static bool TryParseComment(JsonElement element, out InlineComment comment, out string error)
    {
        comment = null!;

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "Comment must be an object.";
            return false;
        }

        if (!TryGetString(element, "path", out var path))
        {
            error = "Comment path must be a string.";
            return false;
        }

        if (!TryGetPositiveInt(element, "line", out var line))
        {
            error = "Comment line must be a positive integer.";
            return false;
        }

        if (!TryGetString(element, "body", out var body))
        {
            error = "Comment body must be a string.";
            return false;
        }

        comment = new InlineComment(
            Path: path,
            Line: line,
            Side: ParseSide(element),
            Body: body,
            Severity: ParseSeverity(element),
            Confidence: ParseConfidence(element));
        error = string.Empty;
        return true;
    }

    private static Confidence ParseConfidence(JsonElement element)
    {
        if (!TryGetString(element, "confidence", out var confidence))
        {
            return Confidence.High;
        }

        return confidence.ToLowerInvariant() switch
        {
            "low" => Confidence.Low,
            "medium" => Confidence.Medium,
            _ => Confidence.High
        };
    }

    private static Severity ParseSeverity(JsonElement element)
    {
        if (!TryGetString(element, "severity", out var severity))
        {
            return Severity.Info;
        }

        return severity.ToLowerInvariant() switch
        {
            "warning" => Severity.Warning,
            "error" => Severity.Error,
            _ => Severity.Info
        };
    }

    private static string ParseSide(JsonElement element)
    {
        if (!TryGetString(element, "side", out var side))
        {
            return "RIGHT";
        }

        return side.ToUpperInvariant() switch
        {
            "LEFT" => "LEFT",
            "RIGHT" => "RIGHT",
            _ => "RIGHT"
        };
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

    private static bool TryGetPositiveInt(JsonElement element, string name, out int value)
    {
        if (TryGetProperty(element, name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value) &&
            value > 0)
        {
            return true;
        }

        value = 0;
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
            }
            else if (current == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return (start, index - start + 1);
                }
            }
        }

        return null;
    }
}

public sealed record ParseResult(bool Success, ReviewResult? Value, string? Error);
