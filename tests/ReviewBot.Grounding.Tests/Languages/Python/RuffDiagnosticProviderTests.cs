using FluentAssertions;
using ReviewBot.Core.Domain;
using ReviewBot.Grounding.Languages.Python;

namespace ReviewBot.Grounding.Tests.Languages.Python;

public class RuffDiagnosticProviderTests
{
    [Fact]
    public void ParseRuffJson_MapsLintRulesAndSyntaxErrorsWithRepoRelativePaths()
    {
        const string root = "/work/clone";
        const string json = """
            [
              {"code":"F401","message":"`os` imported but unused","filename":"/work/clone/app/main.py","location":{"row":3,"column":1}},
              {"code":null,"message":"SyntaxError: invalid syntax","filename":"/work/clone/app/bad.py","location":{"row":7,"column":2}}
            ]
            """;

        var diagnostics = RuffDiagnosticProvider.ParseRuffJson(json, root);

        diagnostics.Should().HaveCount(2);
        diagnostics[0].Should().Be(new Diagnostic("app/main.py", 3, DiagnosticSeverity.Warning, "F401", "`os` imported but unused"));
        diagnostics[1].Should().Be(new Diagnostic("app/bad.py", 7, DiagnosticSeverity.Error, "ruff", "SyntaxError: invalid syntax"));
    }

    [Fact]
    public void ParseRuffJson_ReturnsEmptyForEmptyOrNonArrayOutput()
    {
        RuffDiagnosticProvider.ParseRuffJson("", "/work").Should().BeEmpty();
        RuffDiagnosticProvider.ParseRuffJson("[]", "/work").Should().BeEmpty();
        RuffDiagnosticProvider.ParseRuffJson("{}", "/work").Should().BeEmpty();
    }

    [Fact]
    public async Task GetDiagnosticsAsync_ReturnsEmptyWhenNoPythonFilesChanged()
    {
        var provider = new RuffDiagnosticProvider();

        var diagnostics = await provider.GetDiagnosticsAsync("/work", ["src/App.cs", "README.md"], CancellationToken.None);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDiagnosticsAsync_DegradesGracefullyWhenRuffIsMissing()
    {
        var provider = new RuffDiagnosticProvider();

        // No ruff on PATH in the unlikely event it's installed, point at a nonexistent dir.
        var diagnostics = await provider.GetDiagnosticsAsync(
            Path.Combine(Path.GetTempPath(), $"reviewbot-missing-{Guid.NewGuid():N}"),
            ["app/main.py"],
            CancellationToken.None);

        diagnostics.Should().BeEmpty();
    }
}
