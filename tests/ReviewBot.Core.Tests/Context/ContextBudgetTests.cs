using FluentAssertions;
using ReviewBot.Core.Context;

namespace ReviewBot.Core.Tests.Context;

public sealed class ContextBudgetTests
{
    [Fact]
    public void LeavesReserveUnchangedWhenItFitsTheWindow()
    {
        // The reference profile: 4096 reserve at 32K is well under 32768/4.
        ContextBudget.ResolveResponseReserveTokens(4096, 32_768).Should().Be(4096);
    }

    [Fact]
    public void ClampsReserveDownForSmallContextModels()
    {
        // 4096 on an 8K model would leave too little room for the prompt.
        ContextBudget.ResolveResponseReserveTokens(4096, 8_192).Should().Be(2048);
    }

    [Fact]
    public void CapsAnOversizedReserveAtAQuarterOfTheWindow()
    {
        ContextBudget.ResolveResponseReserveTokens(16_000, 32_768).Should().Be(8192);
    }

    [Fact]
    public void NeverDropsBelowTheMinimumViableReserve()
    {
        // A tiny window still gets a floor so the model can produce a reply.
        ContextBudget.ResolveResponseReserveTokens(4096, 1_000)
            .Should().Be(ContextBudget.MinViableReserveTokens);
    }

    [Fact]
    public void RaisesReserveForLargeContextModels()
    {
        // A 100K reasoning model needs far more than the fixed 4096 default: it spends
        // its output allowance thinking before it answers, and too small a reserve comes
        // back as an empty response. Observed on Qwen3.6-27B at 100K.
        ContextBudget.ResolveResponseReserveTokens(4096, 100_000).Should().Be(12_500);
    }

    [Fact]
    public void KeepsAnExplicitlyLargerReserveOnALargeWindow()
    {
        // The floor only lifts a too-small reserve; a bigger explicit ask still stands.
        ContextBudget.ResolveResponseReserveTokens(20_000, 100_000).Should().Be(20_000);
    }

    [Fact]
    public void StillCapsTheReserveOnALargeWindow()
    {
        ContextBudget.ResolveResponseReserveTokens(90_000, 100_000).Should().Be(25_000);
    }

    [Fact]
    public void LeavesTheReferenceProfileUntouchedAtThirtyTwoK()
    {
        // The 32K floor works out to exactly the 4096 default, so nothing changes for
        // the models the fixed reserve was originally tuned against.
        ContextBudget.ResolveResponseReserveTokens(4096, 32_768).Should().Be(4096);
        ContextBudget.ResolveResponseReserveTokens(4096, 16_384).Should().Be(4096);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PreservesOptOutWhenReserveIsZeroOrNegative(int configured)
    {
        // 0 means "no reserve"; clamping must not turn that into a positive value.
        ContextBudget.ResolveResponseReserveTokens(configured, 8_192).Should().Be(configured);
    }
}
