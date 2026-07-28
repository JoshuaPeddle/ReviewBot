using System.Diagnostics;
using FluentAssertions;
using ReviewBot.Grounding.Workspace;

namespace ReviewBot.Grounding.Tests.Workspace;

public class GitWorkspaceFactoryTests : IDisposable
{
    private readonly List<string> dirsToCleanup = [];

    [Fact]
    public async Task CreateAsyncClonesRepoAndLocalPathExists()
    {
        var originPath = TempDir("origin");
        SetupLocalGitRepo(originPath);
        var sha = GetHeadSha(originPath);

        var factory = new GitWorkspaceFactory();
        var request = new WorkspaceRequest(originPath, sha, "");

        await using var workspace = await factory.CreateAsync(request, CancellationToken.None);

        workspace.Should().NotBeNull();
        workspace.LocalPath.Should().NotBeNullOrEmpty();
        Directory.Exists(workspace.LocalPath).Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsyncClonesExpectedContentsIntoWorkspace()
    {
        var originPath = TempDir("origin-contents");
        SetupLocalGitRepo(originPath);
        File.WriteAllText(Path.Combine(originPath, "MyApp.csproj"), "<Project/>");
        RunGitCommand(originPath, "add", "MyApp.csproj");
        RunGitCommand(originPath, "commit", "-m", "add csproj");
        var sha = GetHeadSha(originPath);

        var factory = new GitWorkspaceFactory();
        var request = new WorkspaceRequest(originPath, sha, "");

        await using var workspace = await factory.CreateAsync(request, CancellationToken.None);

        File.Exists(Path.Combine(workspace.LocalPath, "MyApp.csproj")).Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsyncCleansUpTempDirOnCloneFailure()
    {
        // Give the factory a private root. Scanning the shared system temp directory
        // instead made this test assert on global state: any workspace another test held
        // at that moment looked like an orphan, and it failed roughly one CI run in two on
        // an unchanged commit. A dedicated root makes the assertion exact and deterministic.
        var workspaceRoot = TempDir("workspace-root");
        var factory = new GitWorkspaceFactory(tempRoot: workspaceRoot);
        var request = new WorkspaceRequest("/nonexistent/path/reviewbot-test-repo", "abc1234", "");

        var act = () => factory.CreateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        Directory.GetDirectories(workspaceRoot).Should()
            .BeEmpty("temp workspace must be removed after clone failure");
    }

    [Fact]
    public async Task CreateAsyncCreatesTheWorkspaceUnderTheConfiguredRoot()
    {
        var workspaceRoot = TempDir("workspace-root-success");
        var originPath = TempDir("origin-root");
        SetupLocalGitRepo(originPath);
        var sha = GetHeadSha(originPath);

        var factory = new GitWorkspaceFactory(tempRoot: workspaceRoot);

        await using var workspace = await factory.CreateAsync(
            new WorkspaceRequest(originPath, sha, ""), CancellationToken.None);

        // Pins the seam the cleanup test depends on: if the root were ignored, that test
        // would silently pass by asserting on an empty directory nothing ever used.
        workspace.LocalPath.Should().StartWith(workspaceRoot);
        Directory.GetDirectories(workspaceRoot).Should().ContainSingle();
    }

    [Fact]
    public async Task CreateAsyncThrowsInvalidOperationExceptionWithGitCommandOnCloneFailure()
    {
        var factory = new GitWorkspaceFactory();
        var request = new WorkspaceRequest("/nonexistent/path/reviewbot-test-repo", "abc1234", "");

        var act = () => factory.CreateAsync(request, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("git").And.Contain("failed");
    }

    [Fact]
    public async Task CreateAsyncDisposedWorkspaceRemovesLocalDirectory()
    {
        var originPath = TempDir("origin-dispose");
        SetupLocalGitRepo(originPath);
        var sha = GetHeadSha(originPath);

        var factory = new GitWorkspaceFactory();
        var request = new WorkspaceRequest(originPath, sha, "");

        string localPath;
        await using (var workspace = await factory.CreateAsync(request, CancellationToken.None))
        {
            localPath = workspace.LocalPath;
            Directory.Exists(localPath).Should().BeTrue();
        }

        Directory.Exists(localPath).Should().BeFalse("workspace directory must be deleted on dispose");
    }

    public void Dispose()
    {
        foreach (var dir in dirsToCleanup.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }

    private string TempDir(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"reviewbot-test-{suffix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        dirsToCleanup.Add(path);
        return path;
    }

    private static void SetupLocalGitRepo(string path)
    {
        RunGitCommand(path, "init");
        RunGitCommand(path, "config", "user.email", "test@reviewbot.test");
        RunGitCommand(path, "config", "user.name", "ReviewBot Test");
        File.WriteAllText(Path.Combine(path, "README.md"), "test repo");
        RunGitCommand(path, "add", "README.md");
        RunGitCommand(path, "commit", "-m", "initial commit");
    }

    private static string GetHeadSha(string repoPath)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        p.StartInfo.ArgumentList.Add("rev-parse");
        p.StartInfo.ArgumentList.Add("HEAD");
        p.Start();
        var sha = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return sha;
    }

    private static void RunGitCommand(string workingDir, params string[] args)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
            p.StartInfo.ArgumentList.Add(arg);
        p.Start();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"Test setup git command failed: git {string.Join(" ", args)}");
    }
}
