using FluentAssertions;
using ReviewBot.Core.Domain;
using ReviewBot.Grounding.Languages.Python;

namespace ReviewBot.Grounding.Tests.Languages.Python;

public class PythonTestRunnerTests : IDisposable
{
    private readonly List<string> _dirsToCleanup = [];

    private static readonly GroundingConfig DefaultConfig = new(
        Enabled: true, Build: true, Tests: true, LocalTests: true,
        BuildTimeoutSeconds: 120, TestTimeoutSeconds: 300,
        BuildCommand: null, TestCommand: null);

    private static readonly GroundingConfig ShortTimeoutConfig = DefaultConfig with
    {
        TestTimeoutSeconds = 1
    };

    [Fact]
    public async Task RunAsync_AllPassing_ReturnsPassedCount()
    {
        var dir = CreateWorkspace(
            ("pytest.ini", "[pytest]\n"),
            ("pytest.py", FakePytest("..", "2 passed in 0.01s", exitCode: 0)));

        var runner = new PythonTestRunner();
        var result = await runner.RunAsync(dir, DefaultConfig, CancellationToken.None);

        result.Passed.Should().Be(2);
        result.Failed.Should().Be(0);
        result.Skipped.Should().Be(0);
        result.Output.Should().Contain("2 passed");
    }

    [Fact]
    public async Task RunAsync_OneFailing_ReturnsFailedCount()
    {
        var dir = CreateWorkspace(
            ("pytest.ini", "[pytest]\n"),
            ("pytest.py", FakePytest(".F", "1 failed, 1 passed in 0.01s", exitCode: 1)));

        var runner = new PythonTestRunner();
        var result = await runner.RunAsync(dir, DefaultConfig, CancellationToken.None);

        result.Passed.Should().Be(1);
        result.Failed.Should().Be(1);
        result.Output.Should().Contain("1 failed");
    }

    [Fact]
    public async Task RunAsync_CustomTestCommand_UsesConfiguredCommandWithoutPytestConfig()
    {
        var dir = CreateWorkspace();
        var scriptPath = Path.Combine(dir, "custom test.py");
        File.WriteAllText(scriptPath, """
            import sys
            print("custom test " + sys.argv[1])
            print("5 passed, 1 skipped in 0.01s")
            """);
        var config = DefaultConfig with
        {
            TestCommand = $"python3 \"{scriptPath}\" \"two words\""
        };

        var runner = new PythonTestRunner();
        var result = await runner.RunAsync(dir, config, CancellationToken.None);

        result.Passed.Should().Be(5);
        result.Failed.Should().Be(0);
        result.Skipped.Should().Be(1);
        result.Output.Should().Contain("custom test two words");
    }

    [Fact]
    public async Task RunAsync_SkippedTests_ReturnsSkippedCount()
    {
        var dir = CreateWorkspace(
            ("pytest.ini", "[pytest]\n"),
            ("pytest.py", FakePytest(".s", "1 passed, 1 skipped in 0.01s", exitCode: 0)));

        var runner = new PythonTestRunner();
        var result = await runner.RunAsync(dir, DefaultConfig, CancellationToken.None);

        result.Passed.Should().Be(1);
        result.Failed.Should().Be(0);
        result.Skipped.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_NoPytestConfig_ReturnsZeroCountsWithoutRunningPytest()
    {
        var markerPath = "pytest-was-run.txt";
        var dir = CreateWorkspace(
            ("pytest.py", $$"""
                from pathlib import Path
                Path("{{markerPath}}").write_text("ran")
                print("1 passed in 0.01s")
                """));

        var runner = new PythonTestRunner();
        var result = await runner.RunAsync(dir, DefaultConfig, CancellationToken.None);

        result.Passed.Should().Be(0);
        result.Failed.Should().Be(0);
        result.Skipped.Should().Be(0);
        result.Output.Should().Be("no pytest configuration detected");
        File.Exists(Path.Combine(dir, markerPath)).Should().BeFalse("pytest must not run without config");
    }

    [Fact]
    public async Task RunAsync_TimeoutExpires_ReturnsResultWithoutThrowing()
    {
        var dir = CreateWorkspace(
            ("pytest.ini", "[pytest]\n"),
            ("pytest.py", """
                import time
                time.sleep(10)
                print("1 passed in 10.00s")
                """));

        var runner = new PythonTestRunner();
        var result = await runner.RunAsync(dir, ShortTimeoutConfig, CancellationToken.None);

        result.Passed.Should().Be(0);
        result.Failed.Should().Be(0);
        result.Skipped.Should().Be(0);
        result.Output.Should().Contain("timed out");
    }

    [Fact]
    public async Task RunAsync_ExternalCancellation_Throws()
    {
        var dir = CreateWorkspace(
            ("pytest.ini", "[pytest]\n"),
            ("pytest.py", FakePytest(".", "1 passed in 0.01s", exitCode: 0)));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var runner = new PythonTestRunner();
        var act = () => runner.RunAsync(dir, DefaultConfig, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "external cancellation must propagate, not be swallowed as a timeout");
    }

    [Theory]
    [InlineData("pytest.ini", "[pytest]\n", true)]
    [InlineData("conftest.py", "", true)]
    [InlineData("pyproject.toml", "[tool.pytest.ini_options]\naddopts = '-q'\n", true)]
    [InlineData("setup.cfg", "[tool:pytest]\naddopts = -q\n", true)]
    [InlineData("pyproject.toml", "[tool.black]\nline-length = 88\n", false)]
    public void HasPytestConfig_DetectsConfigCorrectly(string filename, string content, bool expected)
    {
        var dir = CreateWorkspace((filename, content));

        PythonTestRunner.HasPytestConfig(dir).Should().Be(expected);
    }

    public void Dispose()
    {
        foreach (var dir in _dirsToCleanup.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }

    private string CreateWorkspace(params (string Name, string Content)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"reviewbot-python-test-runner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirsToCleanup.Add(dir);
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return dir;
    }

    private static string FakePytest(string progress, string summary, int exitCode) =>
        $$"""
        import sys
        print("{{progress}}")
        print("{{summary}}")
        sys.exit({{exitCode}})
        """;
}
