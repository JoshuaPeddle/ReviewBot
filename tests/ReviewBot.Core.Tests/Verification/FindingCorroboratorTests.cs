using FluentAssertions;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Verification;

namespace ReviewBot.Core.Tests.Verification;

public class FindingCorroboratorTests
{
    private static InlineComment Comment(string path, int line) =>
        new(path, line, "RIGHT", "Possible null dereference.", Severity.Error);

    private static Diagnostic Diag(string path, int line, DiagnosticSeverity sev = DiagnosticSeverity.Error, string code = "CS8602") =>
        new(path, line, sev, code, "Dereference of a possibly null reference.");

    [Fact]
    public void MarksFindingVerifiedWhenADiagnosticSharesPathAndLine()
    {
        var comments = new[] { Comment("src/Foo.cs", 42) };
        var diagnostics = new[] { Diag("src/Foo.cs", 42) };

        var result = FindingCorroborator.Corroborate(comments, diagnostics);

        result.Should().ContainSingle();
        result[0].Comment.Verification.Should().Be(VerificationStatus.Verified);
        result[0].Evidence.Should().Be(diagnostics[0]);
    }

    [Fact]
    public void CorroboratesWithinTheLineTolerance()
    {
        var comments = new[] { Comment("src/Foo.cs", 42) };
        var diagnostics = new[] { Diag("src/Foo.cs", 44) }; // +2, within default tolerance

        var result = FindingCorroborator.Corroborate(comments, diagnostics);

        result[0].Comment.Verification.Should().Be(VerificationStatus.Verified);
    }

    [Fact]
    public void DoesNotCorroborateBeyondTheLineTolerance()
    {
        var comments = new[] { Comment("src/Foo.cs", 42) };
        var diagnostics = new[] { Diag("src/Foo.cs", 46) }; // +4, beyond default tolerance of 2

        var result = FindingCorroborator.Corroborate(comments, diagnostics);

        result[0].Comment.Verification.Should().Be(VerificationStatus.Unverified);
        result[0].Evidence.Should().BeNull();
    }

    [Fact]
    public void DoesNotCorroborateAcrossDifferentFiles()
    {
        var comments = new[] { Comment("src/Foo.cs", 42) };
        var diagnostics = new[] { Diag("src/Bar.cs", 42) };

        var result = FindingCorroborator.Corroborate(comments, diagnostics);

        result[0].Comment.Verification.Should().Be(VerificationStatus.Unverified);
        result[0].Evidence.Should().BeNull();
    }

    [Fact]
    public void PicksTheClosestDiagnosticThenTheMoreSevereOnTies()
    {
        var comments = new[] { Comment("src/Foo.cs", 42) };
        var diagnostics = new[]
        {
            Diag("src/Foo.cs", 41, DiagnosticSeverity.Warning, "CS0168"), // distance 1
            Diag("src/Foo.cs", 43, DiagnosticSeverity.Error, "CS8602"),   // distance 1, more severe
            Diag("src/Foo.cs", 44, DiagnosticSeverity.Error, "CS0103"),   // distance 2
        };

        var result = FindingCorroborator.Corroborate(comments, diagnostics);

        result[0].Evidence!.Code.Should().Be("CS8602");
    }

    [Fact]
    public void LeavesFindingsUnchangedWhenThereAreNoDiagnostics()
    {
        var comments = new[] { Comment("src/Foo.cs", 42) };

        var result = FindingCorroborator.Corroborate(comments, []);

        result[0].Comment.Should().BeSameAs(comments[0]);
        result[0].Evidence.Should().BeNull();
    }
}
