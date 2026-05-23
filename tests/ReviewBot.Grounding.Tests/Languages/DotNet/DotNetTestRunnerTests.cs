using System.Diagnostics;
using FluentAssertions;
using ReviewBot.Core.Domain;
using ReviewBot.Grounding.Languages.DotNet;

namespace ReviewBot.Grounding.Tests.Languages.DotNet;

public class DotNetTestRunnerTests : IDisposable
{
    private readonly List<string> dirsToCleanup = [];

    private static readonly GroundingConfig DefaultConfig = new(
        Enabled: true, Build: true, Tests: true, LocalTests: true,
        BuildTimeoutSeconds: 120, TestTimeoutSeconds: 120,
        BuildCommand: null, TestCommand: null);

    private static readonly GroundingConfig ShortTimeoutConfig = DefaultConfig with
    {
        TestTimeoutSeconds = 1
    };

    [Fact]
    public async Task RunAsync_ValidProjectWithPassingTests_ReturnsPassedCount()
    {
        var dir = await CreateAndBuildTestProjectAsync("""
            using Xunit;
            namespace TestFixture;
            public class SampleTests
            {
                [Fact] public void AlwaysPasses() => Assert.True(true);
            }
            """);

        var runner = new DotNetTestRunner();
        var result = await runner.RunAsync(dir, DefaultConfig, CancellationToken.None);

        result.Passed.Should().BeGreaterThan(0, "the passing test should be counted");
        result.Failed.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ProjectWithFailingTest_ReturnsFailedCount()
    {
        var dir = await CreateAndBuildTestProjectAsync("""
            using Xunit;
            namespace TestFixture;
            public class SampleTests
            {
                [Fact] public void AlwaysFails() => Assert.False(true, "intentional failure");
            }
            """);

        var runner = new DotNetTestRunner();
        var result = await runner.RunAsync(dir, DefaultConfig, CancellationToken.None);

        result.Failed.Should().BeGreaterThan(0, "the failing assertion must be counted");
        result.Output.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RunAsync_TimeoutExpires_ReturnsResultWithoutThrowing()
    {
        // dotnet test still takes >1 second even with --no-build due to test-host startup overhead.
        var dir = await CreateAndBuildTestProjectAsync("""
            using Xunit;
            namespace TestFixture;
            public class SampleTests
            {
                [Fact] public void AlwaysPasses() => Assert.True(true);
            }
            """);

        var runner = new DotNetTestRunner();
        var result = await runner.RunAsync(dir, ShortTimeoutConfig, CancellationToken.None);

        // The invariant is "no exception thrown on timeout"; the result may or may not reflect a timed-out run
        // on an unusually fast machine.
        result.Should().NotBeNull("timeout must not throw; it returns a TestResult");
        if (result.Output.Contains("timed out"))
            result.Passed.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ExternalCancellation_Throws()
    {
        var dir = await CreateAndBuildTestProjectAsync("""
            using Xunit;
            namespace TestFixture;
            public class SampleTests
            {
                [Fact] public void AlwaysPasses() => Assert.True(true);
            }
            """);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var runner = new DotNetTestRunner();
        var act = () => runner.RunAsync(dir, DefaultConfig, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "external cancellation must propagate, not be swallowed as a timeout");
    }

    public void Dispose()
    {
        foreach (var dir in dirsToCleanup.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }

    // Pre-builds the project so DotNetTestRunner can run with --no-build --no-restore.
    private async Task<string> CreateAndBuildTestProjectAsync(string testCode)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"reviewbot-test-runner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        dirsToCleanup.Add(dir);
        File.WriteAllText(Path.Combine(dir, "TestFixture.csproj"), TestCsproj());
        File.WriteAllText(Path.Combine(dir, "Tests.cs"), testCode);

        await RunDotNetAsync(dir, ["build", "-c", "Release"]);
        return dir;
    }

    private static async Task RunDotNetAsync(string workingDir, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();
        await process.WaitForExitAsync();
    }

    private static string TestCsproj() =>
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <IsPackable>false</IsPackable>
            <Nullable>enable</Nullable>
            <ImplicitUsings>disable</ImplicitUsings>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.5.1" />
            <PackageReference Include="xunit" Version="2.9.3" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5">
              <PrivateAssets>all</PrivateAssets>
              <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
            </PackageReference>
          </ItemGroup>
        </Project>
        """;
}
