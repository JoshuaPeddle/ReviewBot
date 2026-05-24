using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewBot.Core.Domain;
using ReviewBot.Grounding.Build;

namespace ReviewBot.Grounding.Languages.Python;

public sealed class PythonBuildRunner : IBuildRunner
{
    private const int OutputMaxLength = 8192;
    private readonly ILogger<PythonBuildRunner> _logger;

    public PythonBuildRunner(ILogger<PythonBuildRunner>? logger = null)
    {
        _logger = logger ?? NullLogger<PythonBuildRunner>.Instance;
    }

    public string LanguageId => "python";

    public async Task<BuildResult> RunAsync(string workspacePath, GroundingConfig config, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(config.BuildTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            if (!string.IsNullOrWhiteSpace(config.BuildCommand))
                return await RunConfiguredCommandAsync(workspacePath, config.BuildCommand, linkedCts.Token);

            return HasMypyConfig(workspacePath)
                ? await RunMypyAsync(workspacePath, linkedCts.Token)
                : await RunCompileAllAsync(workspacePath, linkedCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Python build check timed out after {Seconds}s in {Path}",
                config.BuildTimeoutSeconds, workspacePath);
            return new BuildResult(false, 0, 0, $"Build check timed out after {config.BuildTimeoutSeconds}s");
        }
    }

    private async Task<BuildResult> RunConfiguredCommandAsync(
        string workspacePath, string commandLine, CancellationToken ct)
    {
        if (!ProcessCommand.TryParse(commandLine, out var command, out var error))
            return new BuildResult(false, 0, 0, $"Invalid build command: {error}");

        var (output, exitCode) = await CaptureAsync(workspacePath, command!, ct);
        var errors = Regex.Matches(output, @"(?:\berror\b|SyntaxError)", RegexOptions.IgnoreCase).Count;
        var warnings = Regex.Matches(output, @"\bwarning\b", RegexOptions.IgnoreCase).Count;

        _logger.LogDebug("custom Python build command in {Path}: exit={ExitCode}, errors={Errors}, warnings={Warnings}",
            workspacePath, exitCode, errors, warnings);

        return new BuildResult(exitCode == 0, warnings, errors, Truncate(output));
    }

    internal static bool HasMypyConfig(string workspacePath)
    {
        if (File.Exists(Path.Combine(workspacePath, "mypy.ini")))
            return true;
        if (File.Exists(Path.Combine(workspacePath, ".mypy.ini")))
            return true;

        var pyprojectPath = Path.Combine(workspacePath, "pyproject.toml");
        if (File.Exists(pyprojectPath))
        {
            var content = File.ReadAllText(pyprojectPath);
            if (Regex.IsMatch(content, @"^\[tool\.mypy", RegexOptions.Multiline))
                return true;
        }

        var setupCfgPath = Path.Combine(workspacePath, "setup.cfg");
        if (File.Exists(setupCfgPath))
        {
            var content = File.ReadAllText(setupCfgPath);
            if (Regex.IsMatch(content, @"^\[mypy\]", RegexOptions.Multiline))
                return true;
        }

        return false;
    }

    private async Task<BuildResult> RunMypyAsync(string workspacePath, CancellationToken ct)
    {
        var (output, exitCode) = await CaptureAsync(
            workspacePath,
            new ProcessCommand("python3", ["-m", "mypy", ".", "--no-error-summary", "--no-color-output"]),
            ct);

        var errors = Regex.Matches(output, @":\s+error:", RegexOptions.IgnoreCase).Count;
        var warnings = Regex.Matches(output, @":\s+warning:", RegexOptions.IgnoreCase).Count;

        _logger.LogDebug("mypy in {Path}: exit={ExitCode}, errors={Errors}, warnings={Warnings}",
            workspacePath, exitCode, errors, warnings);

        return new BuildResult(exitCode == 0, warnings, errors, Truncate(output));
    }

    private async Task<BuildResult> RunCompileAllAsync(string workspacePath, CancellationToken ct)
    {
        var (output, exitCode) = await CaptureAsync(
            workspacePath,
            new ProcessCommand("python3", ["-m", "compileall", "-q", "."]),
            ct);

        var errors = Regex.Matches(output, @"SyntaxError", RegexOptions.IgnoreCase).Count;

        _logger.LogDebug("compileall in {Path}: exit={ExitCode}, errors={Errors}",
            workspacePath, exitCode, errors);

        return new BuildResult(exitCode == 0, 0, errors, Truncate(output));
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

    private static string Truncate(string output) =>
        output.Length <= OutputMaxLength
            ? output
            : output[..OutputMaxLength] + "\n[output truncated]";
}
