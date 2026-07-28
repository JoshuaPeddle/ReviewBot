using FluentAssertions;
using ReviewBot.Core.Domain;
using ReviewBot.Grounding.Languages.DotNet;

namespace ReviewBot.Grounding.Tests.Languages.DotNet;

public sealed class RoslynDiagnosticProviderTests : IDisposable
{
    private readonly string workspace =
        Directory.CreateTempSubdirectory("reviewbot-roslyn-").FullName;

    [Fact]
    public async Task GetDiagnosticsAsync_ReturnsNotRun_WhenNoCSharpFilesChanged()
    {
        var provider = new RoslynDiagnosticProvider();

        var report = await provider.GetDiagnosticsAsync(
            this.workspace, ["README.md", "app/main.py"], CancellationToken.None);

        report.ToolRan.Should().BeFalse();
        report.Diagnostics.Should().BeEmpty();
        report.AnalyzedPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDiagnosticsAsync_ExcludesUnsafePaths()
    {
        var provider = new RoslynDiagnosticProvider();

        // All unsafe .cs paths — none is read, so nothing is analyzed and ToolRan stays
        // false (a traversal path must never be treated as proven to parse).
        var report = await provider.GetDiagnosticsAsync(
            this.workspace,
            ["../escape.cs", "/abs/evil.cs", @"a\b.cs"],
            CancellationToken.None);

        report.ToolRan.Should().BeFalse();
        report.AnalyzedPaths.Should().BeEmpty();
        report.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDiagnosticsAsync_ReturnsNotRun_WhenListedFileMissing()
    {
        var provider = new RoslynDiagnosticProvider();

        // Path is safe and .cs, but absent from the checkout (e.g. a deletion): it is
        // skipped, leaving nothing analyzed.
        var report = await provider.GetDiagnosticsAsync(
            this.workspace, ["src/Ghost.cs"], CancellationToken.None);

        report.ToolRan.Should().BeFalse();
        report.AnalyzedPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDiagnosticsAsync_CleanModernFile_ParsesWithNoDiagnostics()
    {
        // Valid modern C# — including the `= ""` assignment historically misread as
        // `= ;` "invalid syntax". A clean parse is exactly what lets verification refute
        // that hallucination, and Preview parsing keeps primary ctors / collection
        // expressions from registering as false syntax errors.
        this.WriteFile("src/Sample.cs", """
            namespace Demo;

            public sealed class Sample(string name)
            {
                public string Name { get; } = name;
                public string Empty { get; } = "";
                public int[] Numbers { get; } = [1, 2, 3];
            }
            """);
        var provider = new RoslynDiagnosticProvider();

        var report = await provider.GetDiagnosticsAsync(
            this.workspace, ["src/Sample.cs"], CancellationToken.None);

        report.ToolRan.Should().BeTrue();
        report.AnalyzedPaths.Should().ContainSingle().Which.Should().Be("src/Sample.cs");
        report.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDiagnosticsAsync_SyntaxError_EmitsErrorDiagnosticOnThatFile()
    {
        this.WriteFile("src/Broken.cs", """
            namespace Demo;

            public sealed class Broken
            {
                public void M()
                {
                    int x = ;
                }
            }
            """);
        var provider = new RoslynDiagnosticProvider();

        var report = await provider.GetDiagnosticsAsync(
            this.workspace, ["src/Broken.cs"], CancellationToken.None);

        report.ToolRan.Should().BeTrue();
        report.AnalyzedPaths.Should().Contain("src/Broken.cs");
        report.Diagnostics.Should().Contain(d =>
            d.Path == "src/Broken.cs" &&
            d.Severity == DiagnosticSeverity.Error &&
            d.Code.StartsWith("CS", StringComparison.Ordinal));
    }

    private void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(this.workspace, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.workspace, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort temp cleanup; a leaked temp dir must not fail the test run.
        }
    }
}
