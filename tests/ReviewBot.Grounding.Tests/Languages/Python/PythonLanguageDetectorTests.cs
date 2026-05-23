using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReviewBot.Grounding.Detection;
using ReviewBot.Grounding.Languages.Python;

namespace ReviewBot.Grounding.Tests.Languages.Python;

public class PythonLanguageDetectorTests
{
    private const string Sha = "abc1234";

    private readonly PythonLanguageDetector _sut = new(NullLogger<PythonLanguageDetector>.Instance);

    // ── CanDetect ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("pyproject.toml")]
    [InlineData("setup.py")]
    [InlineData("setup.cfg")]
    [InlineData("requirements.txt")]
    [InlineData(".python-version")]
    [InlineData("PYPROJECT.TOML")]
    public void CanDetect_ReturnsTrue_WhenPythonSignalPresent(string file)
    {
        _sut.CanDetect([file]).Should().BeTrue();
    }

    [Fact]
    public void CanDetect_ReturnsTrue_WhenPythonSignalAmongOtherFiles()
    {
        _sut.CanDetect(["README.md", "pyproject.toml"]).Should().BeTrue();
    }

    [Theory]
    [InlineData("MyApp.csproj")]
    [InlineData("go.mod")]
    [InlineData("package.json")]
    public void CanDetect_ReturnsFalse_WhenNoPythonSignal(string file)
    {
        _sut.CanDetect([file]).Should().BeFalse();
    }

    [Fact]
    public void CanDetect_ReturnsFalse_WhenEmptyList()
    {
        _sut.CanDetect([]).Should().BeFalse();
    }

    // ── ExtractMetadataAsync: pyproject.toml ──────────────────────────────────

    [Fact]
    public async Task ExtractMetadataAsync_Pyproject_ParsesRequiresPythonAndConstraintFact()
    {
        var reader = Substitute.For<IRepoContentReader>();
        reader.TryReadFileAsync("pyproject.toml", Sha, Arg.Any<CancellationToken>())
            .Returns("""
                [project]
                name = "mypackage"
                requires-python = ">=3.12"
                """);

        var result = await _sut.ExtractMetadataAsync(reader, Sha, CancellationToken.None);

        result.Should().NotBeNull();
        result!.LanguageId.Should().Be("python");
        result.LanguageVersion.Should().Be("3.12");
        result.ToolchainVersion.Should().BeNull();
        result.Facts.Should().Contain("requires-python: >=3.12");
    }

    [Fact]
    public async Task ExtractMetadataAsync_Pyproject_WithMypySection_IncludesMypyFact()
    {
        var reader = Substitute.For<IRepoContentReader>();
        reader.TryReadFileAsync("pyproject.toml", Sha, Arg.Any<CancellationToken>())
            .Returns("""
                [project]
                requires-python = ">=3.12"

                [tool.mypy]
                strict = true
                """);

        var result = await _sut.ExtractMetadataAsync(reader, Sha, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Facts.Should().Contain("mypy configured");
    }

    [Fact]
    public async Task ExtractMetadataAsync_Pyproject_WithAllToolSections_IncludesAllToolFacts()
    {
        var reader = Substitute.For<IRepoContentReader>();
        reader.TryReadFileAsync("pyproject.toml", Sha, Arg.Any<CancellationToken>())
            .Returns("""
                [project]
                requires-python = ">=3.11"

                [tool.mypy]
                strict = true

                [tool.ruff]
                line-length = 88

                [tool.pyright]
                pythonVersion = "3.11"
                """);

        var result = await _sut.ExtractMetadataAsync(reader, Sha, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Facts.Should().Contain("mypy configured");
        result.Facts.Should().Contain("ruff configured");
        result.Facts.Should().Contain("pyright configured");
    }

    [Fact]
    public async Task ExtractMetadataAsync_Pyproject_WithNoRequiresPython_ReturnsNull()
    {
        var reader = Substitute.For<IRepoContentReader>();
        reader.TryReadFileAsync("pyproject.toml", Sha, Arg.Any<CancellationToken>())
            .Returns("""
                [project]
                name = "mypackage"
                version = "1.0.0"
                """);

        var result = await _sut.ExtractMetadataAsync(reader, Sha, CancellationToken.None);

        result.Should().BeNull();
    }

    // ── ExtractMetadataAsync: .python-version ─────────────────────────────────

    [Fact]
    public async Task ExtractMetadataAsync_PythonVersion_ReturnsMajorMinorAndFullToolchainVersion()
    {
        var reader = Substitute.For<IRepoContentReader>();
        reader.TryReadFileAsync("pyproject.toml", Sha, Arg.Any<CancellationToken>())
            .Returns((string?)null);
        reader.TryReadFileAsync(".python-version", Sha, Arg.Any<CancellationToken>())
            .Returns("3.12.2\n");

        var result = await _sut.ExtractMetadataAsync(reader, Sha, CancellationToken.None);

        result.Should().NotBeNull();
        result!.LanguageVersion.Should().Be("3.12");
        result.ToolchainVersion.Should().Be("3.12.2");
        result.Facts.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractMetadataAsync_PythonVersion_MajorMinorOnly_BothVersionsMatch()
    {
        var reader = Substitute.For<IRepoContentReader>();
        reader.TryReadFileAsync("pyproject.toml", Sha, Arg.Any<CancellationToken>())
            .Returns((string?)null);
        reader.TryReadFileAsync(".python-version", Sha, Arg.Any<CancellationToken>())
            .Returns("3.12");

        var result = await _sut.ExtractMetadataAsync(reader, Sha, CancellationToken.None);

        result.Should().NotBeNull();
        result!.LanguageVersion.Should().Be("3.12");
        result.ToolchainVersion.Should().Be("3.12");
    }

    // ── ExtractMetadataAsync: setup.cfg ───────────────────────────────────────

    [Fact]
    public async Task ExtractMetadataAsync_SetupCfg_ParsesPythonRequires()
    {
        var reader = Substitute.For<IRepoContentReader>();
        reader.TryReadFileAsync("pyproject.toml", Sha, Arg.Any<CancellationToken>())
            .Returns((string?)null);
        reader.TryReadFileAsync(".python-version", Sha, Arg.Any<CancellationToken>())
            .Returns((string?)null);
        reader.TryReadFileAsync("setup.cfg", Sha, Arg.Any<CancellationToken>())
            .Returns("""
                [metadata]
                name = mypackage

                [options]
                python_requires = >=3.11
                packages = find:
                """);

        var result = await _sut.ExtractMetadataAsync(reader, Sha, CancellationToken.None);

        result.Should().NotBeNull();
        result!.LanguageVersion.Should().Be("3.11");
        result.ToolchainVersion.Should().BeNull();
        result.Facts.Should().Contain("requires-python: >=3.11");
    }

    // ── ExtractMetadataAsync: priority order ─────────────────────────────────

    [Fact]
    public async Task ExtractMetadataAsync_PyprojectWinsOverPythonVersion()
    {
        var reader = Substitute.For<IRepoContentReader>();
        reader.TryReadFileAsync("pyproject.toml", Sha, Arg.Any<CancellationToken>())
            .Returns("""
                [project]
                requires-python = ">=3.12"
                """);

        var result = await _sut.ExtractMetadataAsync(reader, Sha, CancellationToken.None);

        result.Should().NotBeNull();
        result!.LanguageVersion.Should().Be("3.12");

        // .python-version must not be read when pyproject.toml found
        await reader.DidNotReceive().TryReadFileAsync(".python-version", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractMetadataAsync_PythonVersionWinsOverSetupCfg()
    {
        var reader = Substitute.For<IRepoContentReader>();
        reader.TryReadFileAsync("pyproject.toml", Sha, Arg.Any<CancellationToken>())
            .Returns((string?)null);
        reader.TryReadFileAsync(".python-version", Sha, Arg.Any<CancellationToken>())
            .Returns("3.13.0");

        var result = await _sut.ExtractMetadataAsync(reader, Sha, CancellationToken.None);

        result.Should().NotBeNull();
        result!.LanguageVersion.Should().Be("3.13");

        // setup.cfg must not be read when .python-version found
        await reader.DidNotReceive().TryReadFileAsync("setup.cfg", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── ExtractMetadataAsync: no signal ──────────────────────────────────────

    [Fact]
    public async Task ExtractMetadataAsync_ReturnsNull_WhenNoPythonConfigFiles()
    {
        var reader = Substitute.For<IRepoContentReader>();
        reader.TryReadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await _sut.ExtractMetadataAsync(reader, Sha, CancellationToken.None);

        result.Should().BeNull();
    }
}
