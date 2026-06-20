using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ReviewBot.Llm.OpenAi;

/// <summary>
/// Discovers a model's real context window by querying an OpenAI-compatible
/// server's <c>/models</c> endpoint and reading the vLLM <c>max_model_len</c>
/// field. Results are cached per (base URL, model) for the process lifetime —
/// a served context window does not change while the server is up.
///
/// Every failure path returns <c>null</c> so the worker falls back to the static
/// registry; probing must never fail a review.
/// </summary>
internal sealed class OpenAiModelContextProbe
{
    private static readonly ConcurrentDictionary<string, int> Cache = new(StringComparer.Ordinal);

    // Probing is off the critical reasoning path; keep it short so a slow or
    // unreachable endpoint can't stall the review for long on the first call.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly OpenAiLlmOptions options;
    private readonly ILogger? logger;

    public OpenAiModelContextProbe(OpenAiLlmOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        this.logger = logger;
    }

    public async Task<int?> TryGetContextWindowTokensAsync(string modelName, CancellationToken ct)
    {
        if (options.BaseUrl is null || string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        var cacheKey = $"{options.BaseUrl.AbsoluteUri}\n{modelName}";
        if (Cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        try
        {
            var json = await FetchModelsAsync(ct).ConfigureAwait(false);
            if (json is not null && TryParseMaxModelLen(json, modelName, out var tokens))
            {
                Cache[cacheKey] = tokens;
                logger?.LogInformation(
                    "Detected context window {ContextTokens} for model {ModelName} from {BaseUrl}",
                    tokens,
                    modelName,
                    options.BaseUrl);
                return tokens;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            logger?.LogDebug(
                ex,
                "Could not probe context window for model {ModelName} from {BaseUrl}; falling back to the registry",
                modelName,
                options.BaseUrl);
        }

        return null;
    }

    private async Task<string?> FetchModelsAsync(CancellationToken ct)
    {
        var url = options.BaseUrl!.AbsoluteUri.TrimEnd('/') + "/models";
        using var http = new HttpClient { Timeout = ProbeTimeout };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses an OpenAI-compatible <c>/models</c> response and returns the
    /// <c>max_model_len</c> for the entry whose <c>id</c> matches
    /// <paramref name="modelName"/>. Pure and side-effect free for testability.
    /// </summary>
    internal static bool TryParseMaxModelLen(string json, string modelName, out int tokens)
    {
        tokens = 0;
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var model in data.EnumerateArray())
        {
            if (!model.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String ||
                !string.Equals(id.GetString(), modelName, StringComparison.Ordinal))
            {
                continue;
            }

            if (model.TryGetProperty("max_model_len", out var maxLen) &&
                maxLen.ValueKind == JsonValueKind.Number &&
                maxLen.TryGetInt32(out var value) &&
                value > 0)
            {
                tokens = value;
                return true;
            }

            // Found the model but it doesn't advertise a context window.
            return false;
        }

        return false;
    }
}
