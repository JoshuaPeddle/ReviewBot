using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewBot.Core.Domain;
using ReviewBot.Grounding.Build;
using ReviewBot.Grounding.Diagnostics;

namespace ReviewBot.Grounding.Languages.Python;

/// <summary>
/// Build-free Python diagnostics via <c>ruff</c>. Runs only over the PR's changed
/// <c>.py</c> files and degrades to no diagnostics when ruff is not installed, so
/// it is safe to leave enabled everywhere.
/// </summary>
public sealed class RuffDiagnosticProvider : IDiagnosticProvider
{
    private const int TimeoutSeconds = 60;
    private readonly ILogger<RuffDiagnosticProvider> logger;

    public RuffDiagnosticProvider(ILogger<RuffDiagnosticProvider>? logger = null)
    {
        this.logger = logger ?? NullLogger<RuffDiagnosticProvider>.Instance;
    }

    public string LanguageId => "python";

    public async Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(
        string workspacePath,
        IReadOnlyList<string> changedPaths,
        CancellationToken ct)
    {
        var pythonFiles = changedPaths
            .Where(p => p.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            .Where(IsSafeRelativePath)
            .ToArray();
        if (pythonFiles.Length == 0)
        {
            return Array.Empty<Diagnostic>();
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var args = new List<string> { "check", "--output-format=json", "--quiet" };
            args.AddRange(pythonFiles);
            var output = await CaptureAsync(workspacePath, new ProcessCommand("ruff", args.ToArray()), linkedCts.Token);
            return ParseRuffJson(output, workspacePath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // ruff missing, timed out, or emitted unparseable output — verification
            // proceeds without Python diagnostics rather than failing the review.
            this.logger.LogDebug(ex, "Ruff diagnostics unavailable in {Path}; continuing without them", workspacePath);
            return Array.Empty<Diagnostic>();
        }
    }

    // ruff --output-format=json emits an array of objects:
    //   { "code": "F401", "message": "...", "filename": "/abs/foo.py", "location": { "row": 3, "column": 1 } }
    // A null "code" denotes a syntax error, which we surface as Error; lint rules are Warnings.
    internal static IReadOnlyList<Diagnostic> ParseRuffJson(string json, string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<Diagnostic>();
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Diagnostic>();
        }

        var diagnostics = new List<Diagnostic>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("location", out var location) ||
                !location.TryGetProperty("row", out var row) ||
                row.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var filename = element.TryGetProperty("filename", out var f) ? f.GetString() : null;
            if (string.IsNullOrEmpty(filename))
            {
                continue;
            }

            var code = element.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()!
                : null;
            var message = element.TryGetProperty("message", out var m) ? m.GetString() ?? string.Empty : string.Empty;

            diagnostics.Add(new Diagnostic(
                ToRepoRelative(filename, workspacePath),
                row.GetInt32(),
                code is null ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                code ?? "ruff",
                message));
        }

        return diagnostics;
    }

    // Defence in depth: changed paths come from Git (which forbids `..` and absolute
    // paths in tracked files), but they flow into a subprocess argument, so reject
    // anything that could escape the workspace before handing it to ruff.
    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.StartsWith('/') ||
            path.Contains('\\', StringComparison.Ordinal) ||
            path.StartsWith('-'))
        {
            return false;
        }

        return path.Split('/').All(segment =>
            !string.IsNullOrEmpty(segment) && segment != "." && segment != "..");
    }

    private static string ToRepoRelative(string path, string workspacePath)
    {
        var normalizedPath = path.Replace('\\', '/');
        var normalizedRoot = workspacePath.Replace('\\', '/').TrimEnd('/');
        if (normalizedRoot.Length > 0 &&
            normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.Ordinal))
        {
            return normalizedPath[(normalizedRoot.Length + 1)..];
        }

        return normalizedPath;
    }

    private static async Task<string> CaptureAsync(string workingDir, ProcessCommand command, CancellationToken ct)
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
        _ = await stderrTask;
        return stdout.Trim();
    }
}
