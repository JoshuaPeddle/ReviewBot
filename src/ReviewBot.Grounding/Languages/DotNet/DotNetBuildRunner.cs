using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewBot.Core.Domain;
using ReviewBot.Grounding.Build;

namespace ReviewBot.Grounding.Languages.DotNet;

public sealed class DotNetBuildRunner : IBuildRunner
{
    private const int OutputMaxLength = 8192;
    private readonly ILogger<DotNetBuildRunner> _logger;

    public DotNetBuildRunner(ILogger<DotNetBuildRunner>? logger = null)
    {
        _logger = logger ?? NullLogger<DotNetBuildRunner>.Instance;
    }

    public string LanguageId => "dotnet";

    public async Task<BuildResult> RunAsync(string workspacePath, GroundingConfig config, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(config.BuildTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            if (!string.IsNullOrWhiteSpace(config.BuildCommand))
                return await RunConfiguredCommandAsync(workspacePath, config.BuildCommand, linkedCts.Token);

            var (restoreOut, restoreCode) = await CaptureAsync(
                workspacePath, new ProcessCommand("dotnet", ["restore", "--no-dependencies"]), linkedCts.Token);

            if (restoreCode != 0)
            {
                _logger.LogWarning("dotnet restore failed (exit {Code}) in {Path}", restoreCode, workspacePath);
                return new BuildResult(false, 0, 0, Truncate(restoreOut));
            }

            var (buildOut, buildCode) = await CaptureAsync(
                workspacePath,
                new ProcessCommand("dotnet", ["build", "--no-restore", "-c", "Release", "--no-incremental"]),
                linkedCts.Token);

            var warnings = ParseCount(buildOut, "Warning");
            var errors = ParseCount(buildOut, "Error");

            _logger.LogDebug(
                "dotnet build in {Path}: exit={ExitCode}, warnings={Warnings}, errors={Errors}",
                workspacePath, buildCode, warnings, errors);

            return new BuildResult(buildCode == 0, warnings, errors, Truncate(buildOut));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("dotnet build timed out after {Seconds}s in {Path}",
                config.BuildTimeoutSeconds, workspacePath);
            return new BuildResult(false, 0, 0, $"Build timed out after {config.BuildTimeoutSeconds}s");
        }
    }

    private async Task<BuildResult> RunConfiguredCommandAsync(
        string workspacePath, string commandLine, CancellationToken ct)
    {
        if (!ProcessCommand.TryParse(commandLine, out var command, out var error))
            return new BuildResult(false, 0, 0, $"Invalid build command: {error}");

        var (output, exitCode) = await CaptureAsync(workspacePath, command!, ct);
        var warnings = ParseCount(output, "Warning");
        var errors = ParseCount(output, "Error");

        _logger.LogDebug(
            "custom dotnet build command in {Path}: exit={ExitCode}, warnings={Warnings}, errors={Errors}",
            workspacePath, exitCode, warnings, errors);

        return new BuildResult(exitCode == 0, warnings, errors, Truncate(output));
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

        // Read both streams without cancellation to capture whatever the process emits before we kill it
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

    // Finds the last occurrence of "N Warning(s)" or "N Error(s)" in the MSBuild summary.
    // Using the last match handles multi-project solutions where the final line is the total.
    private static int ParseCount(string output, string type)
    {
        var matches = Regex.Matches(output, $@"(\d+)\s+{Regex.Escape(type)}\(s\)", RegexOptions.IgnoreCase);
        if (matches.Count == 0)
            return 0;
        return int.Parse(matches[^1].Groups[1].Value);
    }

    private static string Truncate(string output) =>
        output.Length <= OutputMaxLength
            ? output
            : output[..OutputMaxLength] + "\n[output truncated]";
}
