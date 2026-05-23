using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReviewBot.Grounding.Detection;
using ReviewBot.Grounding.Languages.DotNet;

namespace ReviewBot.Grounding.Tests.Languages.DotNet;

public class DotNetLanguageDetectorTests
{
    private const string Sha = "abc1234";

    private readonly DotNetLanguageDetector _sut = new(NullLogger<DotNetLanguageDetector>.Instance);

    // ── CanDetect ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("MyApp.csproj")]
    [InlineData("MyApp.sln")]
    [InlineData("MyApp.slnx")]
    [InlineData("Directory.Build.props")]
    [InlineData("MYAPP.CSPROJ")]
    public void CanDetect_ReturnsTrue_WhenDotNetSignalPresent(string file)
    {
        _sut.CanDetect([file]).Should().BeTrue();
    }

    [Fact]
    public void CanDetect_ReturnsTrue_WhenDotNetSignalAmongOtherFiles()
    {
        _sut.CanDetect(["README.md", "MyApp.csproj"]).Should().BeTrue();
    }

    [Theory]
    [InlineData("pyproject.toml")]
    [InlineData("go.mod")]
    [InlineData("package.json")]
    public void CanDetect_ReturnsFalse_WhenNoDotNetSignal(string file)
    {
        _sut.CanDetect([file]).Should().BeFalse();
    }

    [Fact]
    public void CanDetect_ReturnsFalse_WhenEmptyList()
    {
        _sut.CanDetect([]).Should().BeFalse();
    }

    // ── ExtractMetadataAsync: Directory.Build.props ───────────────────────────

    [Fact]
    public async Task ExtractMetadataAsync_DirectoryBuildProps_ParsesVersionAndFacts()
    {
        var reader = Substitute.For<IRepoContentReader>();
        reader.TryReadFileAsync("Directory.Build.props", Sha, Arg.Any<CancellationToken>())
            .Returns("""
                <Project>
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <LangVersion>latest</LangVersion>
                    <Nullable>enable</Nullable>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);
        reader.TryReadFileAsync("global.json", Sha, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await _sut.ExtractMetadataAsync(reader, Sha, CancellationToken.None);

        result.Should().NotBeNull();
        result!.LanguageId.Should().Be("dotnet");
        result.LanguageVersion.Should().Be("10.0");
        result.ToolchainVersion.Should().BeNull();
        result.Facts.Should().Contain("LangVersion: latest");
        result.Facts.Should().Contain("Nullable: enable");
        result.Facts.Should().Contain("TreatWarningsAsErrors: true");
        result.Facts.Should().Contain("ImplicitUsings: enable");

        // should NOT have called ListRootFilesAsync when Directory.Build.props found
        await reader.DidNotReceive().ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractMetadataAsync_DirectoryBuildProps_StripsOsSuffix()
    {
        var reader = Substitute.For<IRepoContentReader>();
        reader.TryReadFileAsync("Directory.Build.props", Sha, Arg.Any<CancellationToken>())
            .Returns("""
                <Project>
                  <PropertyGroup>
                    <TargetFramework>net10.0-windows</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
        reader.TryReadFileAsync("global.json", Sha, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await _sut.ExtractMetadataAsync(reader, Sha, CancellationToken.None);

        result!.LanguageVersion.Should().Be("10.0");
    }

    // ── ExtractMetadataAsync: .csproj fallback ────────────────────────────────

    [Fact]
    public async Task ExtractMetadataAsync_FallsBackToCsproj_WhenNoDirectoryBuildProps()
    {
        var reader = Substitute.For<IRepoContentReader>();
        reader.TryReadFileAsync("Directory.Build.props", Sha, Arg.Any<CancellationToken>())
            .Returns((string?)null);
        reader.ListRootFilesAsync(Sha, Arg.Any<CancellationToken>())
            .Returns(new[] { "README.md", "MyApp.csproj" });
        reader.TryReadFileAsync("MyApp.csproj", Sha, Arg.Any<CancellationToken>())
            .Returns("""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
        reader.TryReadFileAsync("global.json", Sha, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await _sut.ExtractMetadataAsync(reader, Sha, CancellationToken.None);

        result.Should().NotBeNull();
        result!.LanguageVersion.Should().Be("9.0");
        result.Facts.Should().Contain("Nullable: enable");
    }

    // ── ExtractMetadataAsync: global.json SDK version ─────────────────────────

    [Fact]
    public async Task ExtractMetadataAsync_GlobalJson_AppearsInToolchainVersion()
    {
        var reader = Substitute.For<IRepoContentReader>();
        reader.TryReadFileAsync("Directory.Build.props", Sha, Arg.Any<CancellationToken>())
            .Returns("""
                <Project>
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
        reader.TryReadFileAsync("global.json", Sha, Arg.Any<CancellationToken>())
            .Returns("""
                {
                  "sdk": {
                    "version": "10.0.100"
                  }
                }
                """);

        var result = await _sut.ExtractMetadataAsync(reader, Sha, CancellationToken.None);

        result.Should().NotBeNull();
        result!.ToolchainVersion.Should().Be("10.0.100");
    }

    // ── ExtractMetadataAsync: no signal ──────────────────────────────────────

    [Fact]
    public async Task ExtractMetadataAsync_ReturnsNull_WhenNoProjectFilesFound()
    {
        var reader = Substitute.For<IRepoContentReader>();
        reader.TryReadFileAsync("Directory.Build.props", Sha, Arg.Any<CancellationToken>())
            .Returns((string?)null);
        reader.ListRootFilesAsync(Sha, Arg.Any<CancellationToken>())
            .Returns(new[] { "README.md", "src/" });

        var result = await _sut.ExtractMetadataAsync(reader, Sha, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExtractMetadataAsync_ReturnsNull_WhenXmlHasNoTargetFramework()
    {
        var reader = Substitute.For<IRepoContentReader>();
        reader.TryReadFileAsync("Directory.Build.props", Sha, Arg.Any<CancellationToken>())
            .Returns("""
                <Project>
                  <PropertyGroup>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
        reader.TryReadFileAsync("global.json", Sha, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await _sut.ExtractMetadataAsync(reader, Sha, CancellationToken.None);

        result.Should().BeNull();
    }

    // ── ExtractMetadataAsync: malformed XML ───────────────────────────────────

    [Fact]
    public async Task ExtractMetadataAsync_ReturnsNull_WhenXmlMalformed()
    {
        var reader = Substitute.For<IRepoContentReader>();
        reader.TryReadFileAsync("Directory.Build.props", Sha, Arg.Any<CancellationToken>())
            .Returns("<<< not valid xml >>>");
        reader.TryReadFileAsync("global.json", Sha, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await _sut.ExtractMetadataAsync(reader, Sha, CancellationToken.None);

        result.Should().BeNull();
    }

    // ── ExtractMetadataAsync: omitted optional facts ──────────────────────────

    [Fact]
    public async Task ExtractMetadataAsync_OmitsAbsentOptionalFacts()
    {
        var reader = Substitute.For<IRepoContentReader>();
        reader.TryReadFileAsync("Directory.Build.props", Sha, Arg.Any<CancellationToken>())
            .Returns("""
                <Project>
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
        reader.TryReadFileAsync("global.json", Sha, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await _sut.ExtractMetadataAsync(reader, Sha, CancellationToken.None);

        result.Should().NotBeNull();
        result!.LanguageVersion.Should().Be("8.0");
        result.Facts.Should().BeEmpty();
    }
}
