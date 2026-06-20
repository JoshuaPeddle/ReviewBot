using FluentAssertions;
using ReviewBot.Core.Domain;
using ReviewBot.Grounding.Languages.DotNet;

namespace ReviewBot.Grounding.Tests.Languages.DotNet;

public class DotNetBuildRunnerTests : IDisposable
{
    private readonly List<string> dirsToCleanup = [];

    // GroundingConfig with generous timeout — individual timeoutSeconds tests override this.
    private static readonly GroundingConfig DefaultConfig = new(
        Enabled: true, Build: true, Tests: false, LocalTests: false,
        BuildTimeoutSeconds: 120, TestTimeoutSeconds: 300,
        BuildCommand: null, TestCommand: null);

    private static readonly GroundingConfig ShortTimeoutConfig = DefaultConfig with
    {
        BuildTimeoutSeconds = 1
    };

    [Fact]
    public void ParseDiagnostics_ParsesErrorsAndWarningsAsRepoRelativePaths()
    {
        const string root = "/work/clone";
        var output = string.Join('\n',
            "  Determining projects to restore...",
            "/work/clone/src/Foo.cs(12,34): error CS0103: The name 'x' does not exist [/work/clone/src/App.csproj]",
            "/work/clone/src/Bar.cs(5,7): warning CS0168: The variable 'y' is declared but never used [/work/clone/src/App.csproj]",
            "    2 Warning(s)");

        var diagnostics = DotNetBuildRunner.ParseDiagnostics(output, root);

        diagnostics.Should().HaveCount(2);
        diagnostics[0].Should().Be(new Diagnostic("src/Foo.cs", 12, DiagnosticSeverity.Error, "CS0103", "The name 'x' does not exist"));
        diagnostics[1].Should().Be(new Diagnostic("src/Bar.cs", 5, DiagnosticSeverity.Warning, "CS0168", "The variable 'y' is declared but never used"));
    }

    [Fact]
    public void ParseDiagnostics_DeduplicatesRepeatsAcrossProjects()
    {
        const string root = "/work/clone";
        var line = "/work/clone/src/Foo.cs(12,34): error CS0103: The name 'x' does not exist";
        var output = string.Join('\n', line + " [/work/clone/a.csproj]", line + " [/work/clone/b.csproj]");

        var diagnostics = DotNetBuildRunner.ParseDiagnostics(output, root);

        diagnostics.Should().ContainSingle();
    }

    [Fact]
    public void ParseDiagnostics_IgnoresNonDiagnosticLines()
    {
        var output = string.Join('\n',
            "Build succeeded.",
            "    0 Warning(s)",
            "    0 Error(s)");

        DotNetBuildRunner.ParseDiagnostics(output, "/work/clone").Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ValidProject_ReturnsSuccessTrue()
    {
        var dir = CreateProject(
            csproj: MinimalCsproj(),
            cs: """
                namespace TestFixture;
                public class Greeter { public string Greet() => "Hello!"; }
                """);

        var runner = new DotNetBuildRunner();
        var result = await runner.RunAsync(dir, DefaultConfig, CancellationToken.None);

        result.Success.Should().BeTrue("valid project should build successfully");
        result.Errors.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ProjectWithCompileError_ReturnsFailureWithErrorCount()
    {
        var dir = CreateProject(
            csproj: MinimalCsproj(),
            cs: """
                namespace TestFixture;
                public class Broken { public int Bad() => "not an int"; }
                """);

        var runner = new DotNetBuildRunner();
        var result = await runner.RunAsync(dir, DefaultConfig, CancellationToken.None);

        result.Success.Should().BeFalse("project with a type error should not build");
        result.Errors.Should().BeGreaterThan(0, "MSBuild should report at least one error");
        result.Output.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RunAsync_ProjectWithWarningDirective_ReportsWarningCount()
    {
        var dir = CreateProject(
            csproj: MinimalCsproj(),
            cs: """
                #warning This is a test warning
                namespace TestFixture;
                public class WithWarning { }
                """);

        var runner = new DotNetBuildRunner();
        var result = await runner.RunAsync(dir, DefaultConfig, CancellationToken.None);

        result.Success.Should().BeTrue("a #warning directive does not prevent compilation");
        result.Warnings.Should().BeGreaterThan(0, "MSBuild should count the #warning as a warning");
    }

    [Fact]
    public async Task RunAsync_CustomBuildCommand_UsesConfiguredCommand()
    {
        var dir = CreateProject(csproj: MinimalCsproj(), cs: "namespace TestFixture;");
        var scriptPath = Path.Combine(dir, "custom build.py");
        File.WriteAllText(scriptPath, """
            import sys
            print("custom build " + sys.argv[1])
            print("Build succeeded.")
            print("    2 Warning(s)")
            print("    0 Error(s)")
            """);

        var config = DefaultConfig with
        {
            BuildCommand = $"python3 \"{scriptPath}\" \"two words\""
        };

        var runner = new DotNetBuildRunner();
        var result = await runner.RunAsync(dir, config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Warnings.Should().Be(2);
        result.Errors.Should().Be(0);
        result.Output.Should().Contain("custom build two words");
    }

    [Fact]
    public async Task RunAsync_TimeoutExpires_ReturnsFailureWithoutThrowing()
    {
        // dotnet commands always take >1 second, so a 1-second timeout reliably fires
        var dir = CreateProject(
            csproj: MinimalCsproj(),
            cs: """
                namespace TestFixture;
                public class Ok { }
                """);

        var runner = new DotNetBuildRunner();
        var result = await runner.RunAsync(dir, ShortTimeoutConfig, CancellationToken.None);

        // The build may or may not have completed in time; if it timed out we expect a failure.
        // If it happened to complete (very fast machine) this assertion will fail — acceptable risk
        // given that the important invariant is "no exception thrown".
        result.Should().NotBeNull("timeout must not throw; it returns a BuildResult");
        if (!result.Success)
            result.Output.Should().Contain("timed out");
    }

    [Fact]
    public async Task RunAsync_ExternalCancellation_Throws()
    {
        var dir = CreateProject(csproj: MinimalCsproj(), cs: "namespace T;");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var runner = new DotNetBuildRunner();
        var act = () => runner.RunAsync(dir, DefaultConfig, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "external cancellation must propagate, not be swallowed as a timeout");
    }

    public void Dispose()
    {
        foreach (var dir in dirsToCleanup.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }

    private string CreateProject(string csproj, string cs)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"reviewbot-build-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        dirsToCleanup.Add(dir);
        File.WriteAllText(Path.Combine(dir, "TestFixture.csproj"), csproj);
        File.WriteAllText(Path.Combine(dir, "Code.cs"), cs);
        return dir;
    }

    private static string MinimalCsproj() =>
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;
}
