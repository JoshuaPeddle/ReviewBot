using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReviewBot.Core.Llm;
using ReviewBot.Evals;

return await EvalCli.RunAsync(args, Console.Out, Console.Error).ConfigureAwait(false);

public static class EvalCli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync("""
                ReviewBot eval harness

                Usage:
                  dotnet run --project tests/ReviewBot.Evals -- score --fixture <dir> --result <llm-result.json> [--out <score.json>]
                  dotnet run --project tests/ReviewBot.Evals -- score --fixtures <dir> --results <dir> [--out <run.json>]
                  dotnet run --project tests/ReviewBot.Evals -- run-live --fixtures <dir> --results <dir> --base-url <url> --model <model> [--retrieval true|false] [--config <review-bot.yml>] [--api-key-env <env-var>] [--manifest <manifest.json>] [--context-tokens 32768] [--per-fixture-timeout 240] [--request-timeout 180] [--max-tokens 4096] [--index-cache-dir <dir>]
                  dotnet run --project tests/ReviewBot.Evals -- compare <baseline-run.json> <candidate-run.json> [--out <comparison.json>]
                """).ConfigureAwait(false);
            return 0;
        }

        if (string.Equals(args[0], "run-live", StringComparison.OrdinalIgnoreCase))
        {
            return await RunLiveAsync(args, output, error).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "compare", StringComparison.OrdinalIgnoreCase))
        {
            return await RunCompareAsync(args, output, error).ConfigureAwait(false);
        }

        if (!string.Equals(args[0], "score", StringComparison.OrdinalIgnoreCase))
        {
            await error.WriteLineAsync($"Unknown eval command '{args[0]}'.").ConfigureAwait(false);
            return 2;
        }

        return await RunScoreAsync(args, output, error).ConfigureAwait(false);
    }

    private static async Task<int> RunLiveAsync(string[] args, TextWriter output, TextWriter error)
    {
        var fixturesPath = ReadOption(args, "--fixtures");
        var resultsPath = ReadOption(args, "--results");
        var baseUrl = ReadOption(args, "--base-url");
        var model = ReadOption(args, "--model");
        var apiKeyEnv = ReadOption(args, "--api-key-env") ?? "REVIEWBOT_EVAL_OPENAI_API_KEY";
        var configPath = ReadOption(args, "--config");
        var manifestPath = ReadOption(args, "--manifest");
        var retrieval = ParseBool(ReadOption(args, "--retrieval"), defaultValue: false);
        var contextTokens = ParseInt(ReadOption(args, "--context-tokens"), defaultValue: 32768);
        var perFixtureTimeoutSeconds = ParseInt(ReadOption(args, "--per-fixture-timeout"), defaultValue: 240);
        var requestTimeoutSeconds = ParseInt(ReadOption(args, "--request-timeout"), defaultValue: 180);
        var maxTokens = ParseInt(ReadOption(args, "--max-tokens"), defaultValue: 4096);
        var indexCacheDir = ReadOption(args, "--index-cache-dir") ??
            Path.Combine(Path.GetTempPath(), "reviewbot-eval-index", Guid.NewGuid().ToString("N"));

        if (fixturesPath is null || resultsPath is null || baseUrl is null || model is null)
        {
            await error.WriteLineAsync(
                "The run-live command requires --fixtures, --results, --base-url, and --model.")
                .ConfigureAwait(false);
            return 2;
        }

        var apiKey = Environment.GetEnvironmentVariable(apiKeyEnv);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await error.WriteLineAsync(
                $"The run-live command requires API key environment variable {apiKeyEnv}.")
                .ConfigureAwait(false);
            return 2;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBaseUrl))
        {
            await error.WriteLineAsync("--base-url must be an absolute URI.").ConfigureAwait(false);
            return 2;
        }

        try
        {
            manifestPath ??= Path.Combine(resultsPath, "manifest.json");
            var results = await new LiveEvalRunner()
                .RunAsync(
                    new LiveEvalOptions(
                        fixturesPath,
                        resultsPath,
                        manifestPath,
                        parsedBaseUrl,
                        model,
                        apiKey,
                        retrieval,
                        configPath,
                        contextTokens,
                        indexCacheDir,
                        perFixtureTimeoutSeconds,
                        requestTimeoutSeconds,
                        maxTokens),
                    output)
                .ConfigureAwait(false);

            var promptTokens = results.Sum(result => result.TokenUsage?.PromptTokens ?? 0);
            var completionTokens = results.Sum(result => result.TokenUsage?.CompletionTokens ?? 0);
            await output.WriteLineAsync(
                $"Wrote {results.Count} result files to {resultsPath} (prompt_tokens={promptTokens}, completion_tokens={completionTokens}).")
                .ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or ArgumentException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> RunScoreAsync(string[] args, TextWriter output, TextWriter error)
    {
        var fixturePath = ReadOption(args, "--fixture");
        var resultPath = ReadOption(args, "--result");
        var fixturesPath = ReadOption(args, "--fixtures");
        var resultsPath = ReadOption(args, "--results");
        var outputPath = ReadOption(args, "--out");

        var singleFixtureMode = fixturePath is not null || resultPath is not null;
        var fixtureSetMode = fixturesPath is not null || resultsPath is not null;
        if (singleFixtureMode == fixtureSetMode)
        {
            await error.WriteLineAsync("The score command requires either --fixture/--result or --fixtures/--results.").ConfigureAwait(false);
            return 2;
        }

        if (singleFixtureMode && (fixturePath is null || resultPath is null))
        {
            await error.WriteLineAsync("The score command requires --fixture and --result.").ConfigureAwait(false);
            return 2;
        }

        if (fixtureSetMode && (fixturesPath is null || resultsPath is null))
        {
            await error.WriteLineAsync("The score command requires --fixtures and --results.").ConfigureAwait(false);
            return 2;
        }

        try
        {
            if (fixtureSetMode)
            {
                var runScore = await new EvalRunScorer()
                    .ScoreAsync(fixturesPath!, resultsPath!)
                    .ConfigureAwait(false);
                await WriteJsonAsync(runScore, outputPath, output).ConfigureAwait(false);
                return runScore.Passed ? 0 : 1;
            }

            var fixture = new EvalFixtureLoader().Load(fixturePath!);
            var rawResult = await File.ReadAllTextAsync(resultPath!).ConfigureAwait(false);
            var parseResult = LlmResultParser.Parse(rawResult);
            if (!parseResult.Success)
            {
                await error.WriteLineAsync(parseResult.Error).ConfigureAwait(false);
                return 1;
            }

            var score = new RuleBasedScorer().Score(fixture, parseResult.Value!);
            await WriteJsonAsync(score, outputPath, output).ConfigureAwait(false);
            return score.Passed ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> RunCompareAsync(string[] args, TextWriter output, TextWriter error)
    {
        var outputPath = ReadOption(args, "--out");
        var positionalArgs = ReadPositionals(args);

        if (positionalArgs.Length != 2)
        {
            await error.WriteLineAsync("The compare command requires <baseline-run.json> and <candidate-run.json>.").ConfigureAwait(false);
            return 2;
        }

        try
        {
            var baseline = await ReadRunScoreAsync(positionalArgs[0]).ConfigureAwait(false);
            var candidate = await ReadRunScoreAsync(positionalArgs[1]).ConfigureAwait(false);
            var comparison = new EvalRunComparer().Compare(baseline, candidate);

            await WriteCompareTableAsync(comparison, output).ConfigureAwait(false);
            if (outputPath is not null)
            {
                await WriteJsonAsync(comparison, outputPath, TextWriter.Null).ConfigureAwait(false);
            }

            return comparison.RegressedFixtures == 0 ? 0 : 1;
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<EvalRunScore> ReadRunScoreAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        return JsonSerializer.Deserialize<EvalRunScore>(json, JsonOptions) ??
            throw new InvalidDataException($"Eval run '{path}' did not contain a run score.");
    }

    private static async Task WriteCompareTableAsync(EvalRunComparison comparison, TextWriter output)
    {
        await output.WriteLineAsync(
            $"Summary: {comparison.RegressedFixtures} regressed, {comparison.ImprovedFixtures} improved, " +
            $"{comparison.UnchangedFixtures} unchanged, {comparison.AddedFixtures} added, {comparison.RemovedFixtures} removed").ConfigureAwait(false);
        await output.WriteLineAsync(
            $"Precision {FormatScore(comparison.BaselinePrecision)} -> {FormatScore(comparison.CandidatePrecision)} ({FormatDelta(comparison.DeltaPrecision)}), " +
            $"Recall {FormatScore(comparison.BaselineRecall)} -> {FormatScore(comparison.CandidateRecall)} ({FormatDelta(comparison.DeltaRecall)}), " +
            $"F1 {FormatScore(comparison.BaselineF1)} -> {FormatScore(comparison.CandidateF1)} ({FormatDelta(comparison.DeltaF1)})").ConfigureAwait(false);
        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync("Status     Fixture                         Baseline  Candidate  Delta F1").ConfigureAwait(false);
        await output.WriteLineAsync("---------  ------------------------------  --------  ---------  --------").ConfigureAwait(false);

        foreach (var fixture in comparison.Fixtures)
        {
            await output.WriteLineAsync(
                $"{fixture.Status,-9}  {Truncate(fixture.FixtureKey, 30),-30}  " +
                $"{FormatOptionalPass(fixture.BaselinePassed),-8}  {FormatOptionalPass(fixture.CandidatePassed),-9}  " +
                $"{FormatOptionalDelta(fixture.DeltaF1),8}").ConfigureAwait(false);
        }
    }

    private static string FormatOptionalPass(bool? passed) =>
        passed switch
        {
            true => "pass",
            false => "fail",
            _ => "-"
        };

    private static string FormatScore(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatDelta(double value) => value >= 0 ? $"+{FormatScore(value)}" : FormatScore(value);

    private static string FormatOptionalDelta(double? value) => value is null ? "-" : FormatDelta(value.Value);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";

    private static async Task WriteJsonAsync<T>(T value, string? outputPath, TextWriter output)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);

        if (outputPath is null)
        {
            await output.WriteLineAsync(json).ConfigureAwait(false);
            return;
        }

        await File.WriteAllTextAsync(outputPath, json).ConfigureAwait(false);
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool ParseBool(string? value, bool defaultValue) =>
        string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : bool.TryParse(value, out var parsed)
                ? parsed
                : defaultValue;

    private static int ParseInt(string? value, int defaultValue) =>
        string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : defaultValue;

    private static string[] ReadPositionals(string[] args)
    {
        var positionals = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            positionals.Add(args[i]);
        }

        return positionals.ToArray();
    }
}
