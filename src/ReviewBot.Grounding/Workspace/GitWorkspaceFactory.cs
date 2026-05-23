using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReviewBot.Grounding.Workspace;

public sealed class GitWorkspaceFactory : IWorkspaceFactory
{
    private readonly ILogger<GitWorkspaceFactory> logger;

    public GitWorkspaceFactory(ILogger<GitWorkspaceFactory>? logger = null)
    {
        this.logger = logger ?? NullLogger<GitWorkspaceFactory>.Instance;
    }

    public async Task<IWorkspace> CreateAsync(WorkspaceRequest request, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"reviewbot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var remoteUrl = BuildAuthenticatedUrl(request.CloneUrl, request.InstallationToken);

            await RunGitAsync(tempDir, ["init"], ct);
            await RunGitAsync(tempDir, ["remote", "add", "origin", remoteUrl], ct);
            await RunGitAsync(tempDir, ["fetch", "--depth", "1", "origin", request.Sha], ct);
            await RunGitAsync(tempDir, ["checkout", "FETCH_HEAD"], ct);

            logger.LogDebug("Workspace ready: {Sha} from {CloneUrl} at {LocalPath}",
                request.Sha, request.CloneUrl, tempDir);

            return new GitWorkspace(tempDir);
        }
        catch
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
            throw;
        }
    }

    private static string BuildAuthenticatedUrl(string cloneUrl, string installationToken)
    {
        if (string.IsNullOrEmpty(installationToken))
            return cloneUrl;

        if (!cloneUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return cloneUrl;

        var uri = new Uri(cloneUrl);
        return $"https://x-access-token:{installationToken}@{uri.Host}{uri.PathAndQuery}";
    }

    private static async Task RunGitAsync(string workingDir, string[] args, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // Prevent git from hanging waiting for credentials or terminal input
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();

        // Read both streams concurrently to avoid deadlocks on large output
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

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

        if (process.ExitCode != 0)
        {
            var output = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            // Sanitize any embedded credentials before surfacing in the exception
            var safeOutput = SanitizeOutput(output.Trim());
            var cmd = args[0]; // subcommand only; args may contain credentials
            throw new InvalidOperationException(
                $"git {cmd} failed (exit {process.ExitCode}): {safeOutput}");
        }
    }

    // Strips embedded x-access-token credentials from git error messages
    private static string SanitizeOutput(string output) =>
        Regex.Replace(output, @"https://x-access-token:[^@]+@", "https://", RegexOptions.IgnoreCase);
}
