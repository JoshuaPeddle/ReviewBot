using FluentAssertions;
using ReviewBot.Core.Verification;

namespace ReviewBot.Core.Tests.Verification;

public class CompileClaimClassifierTests
{
    [Theory]
    [InlineData("The property initializer `= ;` is invalid C# syntax.")]
    [InlineData("This will not compile because the symbol is undefined.")]
    [InlineData("Syntax error: missing semicolon.")]
    [InlineData("This fails to build.")]
    [InlineData("The file does not parse.")]
    // Modal + infinitive. Only the third-person "fails to compile" was listed, so these
    // went unclassified and therefore unrefuted. The first is verbatim from a review the
    // bot posted on PR #48, wrongly claiming string.Contains(char, StringComparison)
    // does not exist — an error-severity hallucination that ground truth could have
    // killed outright.
    [InlineData("`string.Contains(char)` does not accept a `StringComparison` argument, so this line will fail to compile.")]
    [InlineData("Without the cast this would fail to compile.")]
    [InlineData("That overload may fail to compile on older targets.")]
    [InlineData("The project will fail to build after this rename.")]
    [InlineData("This ends up failing to compile.")]
    public void IsCompileFailureClaim_TrueForCompileAndSyntaxClaims(string body)
    {
        CompileClaimClassifier.IsCompileFailureClaim(body).Should().BeTrue();
    }

    [Theory]
    [InlineData("This could throw a NullReferenceException at runtime.")]
    [InlineData("Consider extracting this into a helper for readability.")]
    [InlineData("The cache is invalidated here, which races with the reader.")]
    [InlineData("This logic is incorrect for empty input.")]
    [InlineData("")]
    [InlineData("   ")]
    public void IsCompileFailureClaim_FalseForLogicStyleAndEmptyClaims(string body)
    {
        CompileClaimClassifier.IsCompileFailureClaim(body).Should().BeFalse();
    }

    [Fact]
    public void IsCompileFailureClaim_FalseForNull()
    {
        CompileClaimClassifier.IsCompileFailureClaim(null).Should().BeFalse();
    }
}
