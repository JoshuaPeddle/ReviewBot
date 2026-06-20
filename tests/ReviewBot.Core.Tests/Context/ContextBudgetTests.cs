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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PreservesOptOutWhenReserveIsZeroOrNegative(int configured)
    {
        // 0 means "no reserve"; clamping must not turn that into a positive value.
        ContextBudget.ResolveResponseReserveTokens(configured, 8_192).Should().Be(configured);
    }
}
