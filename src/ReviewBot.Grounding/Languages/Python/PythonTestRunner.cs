using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewBot.Core.Domain;
using ReviewBot.Grounding.Build;

namespace ReviewBot.Grounding.Languages.Python;

public sealed class PythonTestRunner : ITestRunner
{
    private const int OutputMaxLength = 4096;
    private readonly ILogger<PythonTestRunner> _logger;

    private static readonly Regex SummaryCountRegex = new(
        @"(?<count>\d+)\s+(?<kind>passed|failed|skipped)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public PythonTestRunner(ILogger<PythonTestRunner>? logger = null)
    {
        _logger = logger ?? NullLogger<PythonTestRunner>.Instance;
    }

    public string LanguageId => "python";

    public async Task<TestResult> RunAsync(string workspacePath, GroundingConfig config, CancellationToken ct)
    {
        var hasCustomCommand = !string.IsNullOrWhiteSpace(config.TestCommand);
        if (!hasCustomCommand && !HasPytestConfig(workspacePath))
            return new TestResult(0, 0, 0, "no pytest configuration detected");

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(config.TestTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            if (hasCustomCommand)
                return await RunConfiguredCommandAsync(workspacePath, config.TestCommand!, linkedCts.Token);

            var (output, exitCode) = await CaptureAsync(
                workspacePath,
                new ProcessCommand("python3", ["-m", "pytest", "--tb=no", "-q", "--no-header"]),
                linkedCts.Token);

            var (passed, failed, skipped) = ParseSummary(output);

            _logger.LogDebug(
                "pytest in {Path}: exit={ExitCode}, passed={Passed}, failed={Failed}, skipped={Skipped}",
                workspacePath, exitCode, passed, failed, skipped);

            return new TestResult(passed, failed, skipped, Truncate(output));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("pytest timed out after {Seconds}s in {Path}",
                config.TestTimeoutSeconds, workspacePath);
            return new TestResult(0, 0, 0, "pytest timed out");
        }
    }

    private async Task<TestResult> RunConfiguredCommandAsync(
        string workspacePath, string commandLine, CancellationToken ct)
    {
        if (!ProcessCommand.TryParse(commandLine, out var command, out var error))
            return new TestResult(0, 0, 0, $"Invalid test command: {error}");

        var (output, exitCode) = await CaptureAsync(workspacePath, command!, ct);
        var (passed, failed, skipped) = ParseSummary(output);

        _logger.LogDebug(
            "custom Python test command in {Path}: exit={ExitCode}, passed={Passed}, failed={Failed}, skipped={Skipped}",
            workspacePath, exitCode, passed, failed, skipped);

        return new TestResult(passed, failed, skipped, Truncate(output));
    }

    internal static bool HasPytestConfig(string workspacePath)
    {
        if (File.Exists(Path.Combine(workspacePath, "pytest.ini")))
            return true;

        if (File.Exists(Path.Combine(workspacePath, "conftest.py")))
            return true;

        var pyprojectPath = Path.Combine(workspacePath, "pyproject.toml");
        if (File.Exists(pyprojectPath))
        {
            var content = File.ReadAllText(pyprojectPath);
            if (Regex.IsMatch(content, @"^\[tool\.pytest\.ini_options\]", RegexOptions.Multiline))
                return true;
        }

        var setupCfgPath = Path.Combine(workspacePath, "setup.cfg");
        if (File.Exists(setupCfgPath))
        {
            var content = File.ReadAllText(setupCfgPath);
            if (Regex.IsMatch(content, @"^\[tool:pytest\]", RegexOptions.Multiline))
                return true;
        }

        return false;
    }

    private static async Task<(string Output, int ExitCode)> CaptureAsync(
        string workingDir, ProcessCommand command, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in command.Arguments)
            process.StartInfo.ArgumentList.Add(arg);

        process.StartInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
        return (combined.Trim(), process.ExitCode);
    }

    private static (int Passed, int Failed, int Skipped) ParseSummary(string output)
    {
        var passed = 0;
        var failed = 0;
        var skipped = 0;

        foreach (Match match in SummaryCountRegex.Matches(output))
        {
            var count = int.Parse(match.Groups["count"].Value);
            switch (match.Groups["kind"].Value.ToLowerInvariant())
            {
                case "passed":
                    passed = count;
                    break;
                case "failed":
                    failed = count;
                    break;
                case "skipped":
                    skipped = count;
                    break;
            }
        }

        return (passed, failed, skipped);
    }

    private static string Truncate(string output) =>
        output.Length <= OutputMaxLength
            ? output
            : output[..OutputMaxLength] + "\n[output truncated]";
}
