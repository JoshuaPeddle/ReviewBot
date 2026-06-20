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
