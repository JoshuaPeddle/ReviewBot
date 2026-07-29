using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReviewBot.Core.Llm;
using ReviewBot.Evals;
using ReviewBot.Llm.OpenAi;

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
                  dotnet run --project tests/ReviewBot.Evals -- run-live --fixtures <dir> --results <dir> --base-url <url> --model <model> [--retrieval true|false] [--config <review-bot.yml>] [--api-key-env <env-var>] [--manifest <manifest.json>] [--context-tokens 32768] [--per-fixture-timeout 240] [--request-timeout 180] [--max-tokens 4096] [--temperature 0.2] [--top-p 0.95] [--top-k 20] [--min-p 0.0] [--presence-penalty 0.0] [--repetition-penalty 1.0] [--seed 1] [--self-critique true|false] [--index-cache-dir <dir>]
                  dotnet run --project tests/ReviewBot.Evals -- compare <baseline-run.json> <candidate-run.json> [--out <comparison.json>]
                  dotnet run --project tests/ReviewBot.Evals -- aggregate <run-1.json> <run-2.json> [<run-n.json> ...] [--out <aggregate.json>]
                """).ConfigureAwait(false);
            return 0;
        }

        if (string.Equals(args[0], "aggregate", StringComparison.OrdinalIgnoreCase))
        {
            return await RunAggregateAsync(args, output, error).ConfigureAwait(false);
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
        var selfCritique = ParseBool(ReadOption(args, "--self-critique"), defaultValue: false);
        var contextTokens = ParseInt(ReadOption(args, "--context-tokens"), defaultValue: 32768);
        var perFixtureTimeoutSeconds = ParseInt(ReadOption(args, "--per-fixture-timeout"), defaultValue: 240);
        var requestTimeoutSeconds = ParseInt(ReadOption(args, "--request-timeout"), defaultValue: 180);
        var maxTokens = ParseInt(ReadOption(args, "--max-tokens"), defaultValue: 4096);
        var temperature = ParseFloat(ReadOption(args, "--temperature"), defaultValue: 0.2f);
        var indexCacheDir = ReadOption(args, "--index-cache-dir") ??
            Path.Combine(Path.GetTempPath(), "reviewbot-eval-index", Guid.NewGuid().ToString("N"));

        // Optional sampling knobs. Each one left unset is omitted from the request so
        // the server's own default applies — an eval run that passes none of these is
        // byte-identical to one from before these flags existed.
        var invalidSamplingOptions = new List<string>();
        var sampling = new OpenAiSamplingOptions
        {
            TopP = ParseOptionalFloat(args, "--top-p", invalidSamplingOptions),
            TopK = ParseOptionalInt(args, "--top-k", invalidSamplingOptions),
            MinP = ParseOptionalFloat(args, "--min-p", invalidSamplingOptions),
            PresencePenalty = ParseOptionalFloat(args, "--presence-penalty", invalidSamplingOptions),
            RepetitionPenalty = ParseOptionalFloat(args, "--repetition-penalty", invalidSamplingOptions),
            Seed = ParseOptionalLong(args, "--seed", invalidSamplingOptions),
        };

        if (invalidSamplingOptions.Count > 0)
        {
            // Falling back to a default here would silently run the eval with sampling
            // the caller didn't ask for, so refuse instead.
            await error.WriteLineAsync(
                $"Non-numeric value for {string.Join(", ", invalidSamplingOptions)}.")
                .ConfigureAwait(false);
            return 2;
        }

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
                        maxTokens,
                        temperature,
                        selfCritique,
                        sampling.HasAnyValue ? sampling : null),
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

            var verified = await new EvalVerifier().VerifyAsync(fixture, parseResult.Value!).ConfigureAwait(false);
            var score = new RuleBasedScorer().Score(fixture, verified);
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
        var baselinePaths = ReadOptions(args, "--baseline");
        var candidatePaths = ReadOptions(args, "--candidate");

        // Positional form kept for the single-run case: `compare a.json b.json`.
        if (baselinePaths.Length == 0 && candidatePaths.Length == 0)
        {
            var positionalArgs = ReadPositionals(args);
            if (positionalArgs.Length != 2)
            {
                await error.WriteLineAsync(
                    "The compare command requires <baseline-run.json> and <candidate-run.json>, "
                    + "or repeated --baseline/--candidate options.").ConfigureAwait(false);
                return 2;
            }

            baselinePaths = [positionalArgs[0]];
            candidatePaths = [positionalArgs[1]];
        }

        if (baselinePaths.Length == 0 || candidatePaths.Length == 0)
        {
            await error.WriteLineAsync("Both --baseline and --candidate must be given at least once.")
                .ConfigureAwait(false);
            return 2;
        }

        try
        {
            var baselineRuns = new List<EvalRunScore>();
            foreach (var path in baselinePaths)
            {
                baselineRuns.Add(await ReadRunScoreAsync(path).ConfigureAwait(false));
            }

            var candidateRuns = new List<EvalRunScore>();
            foreach (var path in candidatePaths)
            {
                candidateRuns.Add(await ReadRunScoreAsync(path).ConfigureAwait(false));
            }

            // The per-fixture table still compares one run against one run, because a
            // fixture either passed or did not in a given run. The spread verdict below
            // is what says whether the headline delta means anything.
            var comparison = new EvalRunComparer().Compare(baselineRuns[0], candidateRuns[0]);
            await WriteCompareTableAsync(comparison, output).ConfigureAwait(false);
            if (outputPath is not null)
            {
                await WriteJsonAsync(comparison, outputPath, TextWriter.Null).ConfigureAwait(false);
            }

            if (baselineRuns.Count > 1 || candidateRuns.Count > 1)
            {
                await WriteSpreadVerdictAsync(baselineRuns, candidateRuns, output).ConfigureAwait(false);
            }
            else
            {
                await output.WriteLineAsync().ConfigureAwait(false);
                await output.WriteLineAsync(
                    "NOTE: one run per arm. This corpus moves by about 2 fixtures between identical "
                    + "runs, so a delta of that size is not evidence. Pass --baseline/--candidate "
                    + "repeatedly to compare spreads.").ConfigureAwait(false);
            }

            return comparison.RegressedFixtures == 0 ? 0 : 1;
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    /// Reports each arm's mean and range, and how many fixtures are unstable across all
    /// the runs pooled together.
    /// </summary>
    /// <remarks>
    /// Deliberately does not emit a significant/not-significant verdict. The obvious rule
    /// — "delta beats the within-arm range" — is wrong at these sample sizes, and wrong in
    /// the dangerous direction. Two arms of this corpus each scored an identical F1 across
    /// their runs while failing *different* fixtures each time, so the within-arm range was
    /// zero and any threshold built on it called a provably inert change a regression.
    /// The pooled instability count is the honest summary: it says how many fixtures are
    /// capable of moving on their own, and the reader weighs the delta against that.
    /// </remarks>
    private static async Task WriteSpreadVerdictAsync(
        IReadOnlyList<EvalRunScore> baselineRuns,
        IReadOnlyList<EvalRunScore> candidateRuns,
        TextWriter output)
    {
        var aggregator = new EvalRunAggregator();
        var baseline = aggregator.Aggregate(baselineRuns);
        var candidate = aggregator.Aggregate(candidateRuns);
        var pooled = aggregator.Aggregate([.. baselineRuns, .. candidateRuns]);
        var deltaF1 = candidate.F1.Mean - baseline.F1.Mean;
        var deltaPassed = candidate.PassedFixtures.Mean - baseline.PassedFixtures.Mean;

        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync(
            $"Baseline  n={baseline.Runs}  F1 {FormatScore(baseline.F1.Mean)} "
            + $"[{FormatScore(baseline.F1.Min)}-{FormatScore(baseline.F1.Max)}]  "
            + $"passed {FormatScore(baseline.PassedFixtures.Mean)}/{baseline.TotalFixtures}").ConfigureAwait(false);
        await output.WriteLineAsync(
            $"Candidate n={candidate.Runs}  F1 {FormatScore(candidate.F1.Mean)} "
            + $"[{FormatScore(candidate.F1.Min)}-{FormatScore(candidate.F1.Max)}]  "
            + $"passed {FormatScore(candidate.PassedFixtures.Mean)}/{candidate.TotalFixtures}").ConfigureAwait(false);
        await output.WriteLineAsync(
            $"Delta     mean F1 {FormatDelta(deltaF1)}, mean passed {FormatDelta(deltaPassed)} fixture(s)")
            .ConfigureAwait(false);

        var unstable = pooled.UnstableFixtures.Count;
        await output.WriteLineAsync(
            unstable == 0
                ? $"Across all {pooled.Runs} runs pooled, every fixture was stable."
                : $"Across all {pooled.Runs} runs pooled, {unstable} of {pooled.TotalFixtures} fixture(s) flipped on "
                  + $"their own. Weigh the {FormatDelta(deltaPassed)}-fixture delta against that before calling it an effect.")
            .ConfigureAwait(false);
    }

    private static async Task<int> RunAggregateAsync(string[] args, TextWriter output, TextWriter error)
    {
        var outputPath = ReadOption(args, "--out");
        var runPaths = ReadPositionals(args);

        if (runPaths.Length < 2)
        {
            await error.WriteLineAsync(
                "The aggregate command requires at least two run score files. One run cannot show a spread.")
                .ConfigureAwait(false);
            return 2;
        }

        try
        {
            var runs = new List<EvalRunScore>(runPaths.Length);
            foreach (var path in runPaths)
            {
                runs.Add(await ReadRunScoreAsync(path).ConfigureAwait(false));
            }

            var aggregate = new EvalRunAggregator().Aggregate(runs);
            await WriteAggregateTableAsync(aggregate, output).ConfigureAwait(false);
            if (outputPath is not null)
            {
                await WriteJsonAsync(aggregate, outputPath, TextWriter.Null).ConfigureAwait(false);
            }

            return 0;
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task WriteAggregateTableAsync(EvalRunAggregate aggregate, TextWriter output)
    {
        await output.WriteLineAsync($"Aggregate over {aggregate.Runs} run(s) of {aggregate.TotalFixtures} fixture(s)")
            .ConfigureAwait(false);
        if (aggregate.AbortedFixtures > 0)
        {
            await output.WriteLineAsync($"  {aggregate.AbortedFixtures} fixture run(s) aborted and were excluded")
                .ConfigureAwait(false);
        }

        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync("Metric          Mean     Min      Max      Range").ConfigureAwait(false);
        await output.WriteLineAsync("--------------  -------  -------  -------  -------").ConfigureAwait(false);
        await WriteMetricRowAsync(output, "precision", aggregate.Precision).ConfigureAwait(false);
        await WriteMetricRowAsync(output, "recall", aggregate.Recall).ConfigureAwait(false);
        await WriteMetricRowAsync(output, "f1", aggregate.F1).ConfigureAwait(false);
        await WriteMetricRowAsync(output, "passed", aggregate.PassedFixtures).ConfigureAwait(false);

        var unstable = aggregate.UnstableFixtures;
        await output.WriteLineAsync().ConfigureAwait(false);
        if (unstable.Count == 0)
        {
            await output.WriteLineAsync("Every fixture was stable across these runs.").ConfigureAwait(false);
            return;
        }

        // The spread above is produced by these fixtures. Naming them stops the next
        // reader from reading a difference smaller than the spread as a real effect.
        await output.WriteLineAsync(
            $"{unstable.Count} fixture(s) flipped between runs — a delta smaller than the range above is not resolvable:")
            .ConfigureAwait(false);
        foreach (var rate in unstable)
        {
            await output.WriteLineAsync($"  {rate.Passed}/{rate.Runs}  {rate.FixtureName}").ConfigureAwait(false);
        }
    }

    private static Task WriteMetricRowAsync(TextWriter output, string name, MetricSpread spread) =>
        output.WriteLineAsync(
            $"{name,-14}  {FormatScore(spread.Mean),7}  {FormatScore(spread.Min),7}  " +
            $"{FormatScore(spread.Max),7}  {FormatScore(spread.Range),7}");

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

    /// <summary>All values given for a repeatable option, in the order they appeared.</summary>
    private static string[] ReadOptions(string[] args, string name)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                values.Add(args[i + 1]);
            }
        }

        return values.ToArray();
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

    private static float ParseFloat(string? value, float defaultValue) =>
        string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : defaultValue;

    /// <summary>
    /// Reads an optional numeric flag. Absent returns null (send nothing); a value that
    /// isn't a number records the flag name in <paramref name="invalidOptions"/> so the
    /// caller can reject it rather than quietly substituting a default.
    /// </summary>
    private static float? ParseOptionalFloat(string[] args, string name, List<string> invalidOptions)
    {
        var raw = ReadOption(args, name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            invalidOptions.Add(name);
            return null;
        }

        return parsed;
    }

    private static long? ParseOptionalLong(string[] args, string name, List<string> invalidOptions)
    {
        var raw = ReadOption(args, name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            invalidOptions.Add(name);
            return null;
        }

        return parsed;
    }

    private static int? ParseOptionalInt(string[] args, string name, List<string> invalidOptions)
    {
        var raw = ReadOption(args, name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            invalidOptions.Add(name);
            return null;
        }

        return parsed;
    }

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
