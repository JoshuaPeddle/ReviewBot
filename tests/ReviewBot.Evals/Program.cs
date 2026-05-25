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
                """).ConfigureAwait(false);
            return 0;
        }

        if (!string.Equals(args[0], "score", StringComparison.OrdinalIgnoreCase))
        {
            await error.WriteLineAsync($"Unknown eval command '{args[0]}'.").ConfigureAwait(false);
            return 2;
        }

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
}
