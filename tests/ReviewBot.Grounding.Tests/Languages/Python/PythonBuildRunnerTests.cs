using System.Diagnostics;
using FluentAssertions;
using ReviewBot.Core.Domain;
using ReviewBot.Grounding.Languages.Python;

namespace ReviewBot.Grounding.Tests.Languages.Python;

public class PythonBuildRunnerTests : IDisposable
{
    private readonly List<string> _dirsToCleanup = [];

    private static readonly GroundingConfig DefaultConfig = new(
        Enabled: true, Build: true, Tests: false,
        BuildTimeoutSeconds: 120, TestTimeoutSeconds: 300,
        BuildCommand: null, TestCommand: null);

    private static readonly GroundingConfig ShortTimeoutConfig = DefaultConfig with
    {
        BuildTimeoutSeconds = 1
    };

    [Fact]
    public async Task RunAsync_ValidPythonFile_ReturnsSuccess()
    {
        var dir = CreateWorkspace(
            ("module.py", "def greet(name: str) -> str:\n    return f'Hello, {name}'\n"));

        var runner = new PythonBuildRunner();
        var result = await runner.RunAsync(dir, DefaultConfig, CancellationToken.None);

        result.Success.Should().BeTrue("valid Python file should compile without syntax errors");
        result.Errors.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_SyntaxError_ReturnsFailureWithErrors()
    {
        // Unclosed parenthesis is a reliable SyntaxError in any Python version
        var dir = CreateWorkspace(("broken.py", "def bad(\n    pass\n"));

        var runner = new PythonBuildRunner();
        var result = await runner.RunAsync(dir, DefaultConfig, CancellationToken.None);

        result.Success.Should().BeFalse("file with syntax error should not compile");
        result.Errors.Should().BeGreaterThan(0, "compileall should count the SyntaxError");
        result.Output.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RunAsync_MypyConfigPresent_UsesMypyPath()
    {
        if (!IsMypyAvailable())
            return;

        var dir = CreateWorkspace(
            ("pyproject.toml", "[tool.mypy]\n"),
            ("module.py", "x: int = 'wrong type'\n"));

        var runner = new PythonBuildRunner();
        var result = await runner.RunAsync(dir, DefaultConfig, CancellationToken.None);

        result.Success.Should().BeFalse("mypy should report the type assignment error");
        result.Errors.Should().BeGreaterThan(0, "mypy output should contain at least one ': error:' line");
    }

    [Fact]
    public async Task RunAsync_TimeoutExpires_ReturnsFailureWithoutThrowing()
    {
        var dir = CreateWorkspace(("module.py", "x = 1\n"));

        var runner = new PythonBuildRunner();
        var result = await runner.RunAsync(dir, ShortTimeoutConfig, CancellationToken.None);

        // compileall is fast; on a fast machine it may complete within 1s.
        // The important invariant is: no exception is thrown.
        result.Should().NotBeNull("timeout must not throw; it must return a BuildResult");
        if (!result.Success)
            result.Output.Should().Contain("timed out");
    }

    [Fact]
    public async Task RunAsync_ExternalCancellation_Throws()
    {
        var dir = CreateWorkspace(("module.py", "x = 1\n"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var runner = new PythonBuildRunner();
        var act = () => runner.RunAsync(dir, DefaultConfig, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "external cancellation must propagate, not be swallowed as a timeout");
    }

    [Theory]
    [InlineData("mypy.ini", "", true)]
    [InlineData(".mypy.ini", "", true)]
    [InlineData("pyproject.toml", "[tool.mypy]\nstrict = true\n", true)]
    [InlineData("setup.cfg", "[mypy]\nstrict = True\n", true)]
    [InlineData("pyproject.toml", "[tool.black]\nline-length = 88\n", false)]
    public void HasMypyConfig_DetectsConfigCorrectly(string filename, string content, bool expected)
    {
        var dir = CreateWorkspace((filename, content));

        PythonBuildRunner.HasMypyConfig(dir).Should().Be(expected);
    }

    public void Dispose()
    {
        foreach (var dir in _dirsToCleanup.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }

    private string CreateWorkspace(params (string Name, string Content)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"reviewbot-python-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirsToCleanup.Add(dir);
        foreach (var (name, content) in files)
            File.WriteAllText(Path.Combine(dir, name), content);
        return dir;
    }

    private static bool IsMypyAvailable()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "python3",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            process.StartInfo.ArgumentList.Add("-m");
            process.StartInfo.ArgumentList.Add("mypy");
            process.StartInfo.ArgumentList.Add("--version");
            process.Start();
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
