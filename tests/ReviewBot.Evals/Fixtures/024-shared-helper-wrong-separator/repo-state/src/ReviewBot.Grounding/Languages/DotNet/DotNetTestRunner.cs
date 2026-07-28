using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewBot.Core.Domain;
using ReviewBot.Grounding.Build;

namespace ReviewBot.Grounding.Languages.DotNet;

public sealed class DotNetTestRunner : ITestRunner
{
    private const int OutputMaxLength = 4096;
    private readonly ILogger<DotNetTestRunner> _logger;

    // Matches the VSTest/MSBuild aggregate summary line, e.g.:
    // "Passed! - Failed: 0, Passed: 42, Skipped: 3"
    // "Failed! - Failed: 1, Passed: 38, Skipped: 0"
    // The last match is used to get the solution-level total in multi-project solutions.
    private static readonly Regex SummaryRegex = new(
        @"(?:Passed|Failed)!\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public DotNetTestRunner(ILogger<DotNetTestRunner>? logger = null)
    {
        _logger = logger ?? NullLogger<DotNetTestRunner>.Instance;
    }

    public string LanguageId => "dotnet";

    public async Task<TestResult> RunAsync(string workspacePath, GroundingConfig config, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(config.TestTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            if (!string.IsNullOrWhiteSpace(config.TestCommand))
                return await RunConfiguredCommandAsync(workspacePath, config.TestCommand, linkedCts.Token);

            var (output, exitCode) = await CaptureAsync(
                workspacePath,
                new ProcessCommand("dotnet", ["test", "--no-build", "--no-restore", "-c", "Release"]),
                linkedCts.Token);

            var (passed, failed, skipped) = ParseSummary(output);

            _logger.LogDebug(
                "dotnet test in {Path}: exit={ExitCode}, passed={Passed}, failed={Failed}, skipped={Skipped}",
                workspacePath, exitCode, passed, failed, skipped);

            return new TestResult(passed, failed, skipped, Truncate(output));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("dotnet test timed out after {Seconds}s in {Path}",
                config.TestTimeoutSeconds, workspacePath);
            return new TestResult(0, 0, 0, "dotnet test timed out");
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
            "custom dotnet test command in {Path}: exit={ExitCode}, passed={Passed}, failed={Failed}, skipped={Skipped}",
            workspacePath, exitCode, passed, failed, skipped);

        return new TestResult(passed, failed, skipped, Truncate(output));
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

        process.Start();

        // Read both streams without cancellation to capture whatever the process emits before we kill it.
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
        var matches = SummaryRegex.Matches(output);
        if (matches.Count == 0)
            return (0, 0, 0);

        var last = matches[^1];
        return (
            Passed: int.Parse(last.Groups[2].Value),
            Failed: int.Parse(last.Groups[1].Value),
            Skipped: int.Parse(last.Groups[3].Value));
    }

    private static string Truncate(string output) =>
        output.Length <= OutputMaxLength
            ? output
            : output[..OutputMaxLength] + "\n[output truncated]";
}
