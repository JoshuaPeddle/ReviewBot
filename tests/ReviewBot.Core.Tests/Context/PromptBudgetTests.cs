using FluentAssertions;
using ReviewBot.Core.Context;

namespace ReviewBot.Core.Tests.Context;

public class PromptBudgetTests
{
    [Fact]
    public void CreateSubtractsFixedPromptSectionsFromModelLimit()
    {
        var budget = PromptBudget.Create(
            modelContextLimitTokens: 32_768,
            systemPromptTokens: 3_000,
            groundingTokens: 1_500,
            responseReserveTokens: 4_096);

        budget.ContentBudgetTokens.Should().Be(24_172);
        budget.RemainingContentTokens.Should().Be(24_172);
    }

    [Fact]
    public void CreateClampsContentBudgetToZeroWhenFixedSectionsExceedLimit()
    {
        var budget = PromptBudget.Create(
            modelContextLimitTokens: 8_192,
            systemPromptTokens: 5_000,
            groundingTokens: 2_000,
            responseReserveTokens: 4_096);

        budget.ContentBudgetTokens.Should().Be(0);
        budget.RemainingContentTokens.Should().Be(0);
    }

    [Fact]
    public void TryConsumeTracksSectionUsageAndStopsWhenBudgetIsExhausted()
    {
        var budget = PromptBudget.Create(
            modelContextLimitTokens: 100,
            systemPromptTokens: 10,
            groundingTokens: 20,
            responseReserveTokens: 30);

        var consumedDiff = budget.TryConsume("diff", 35, out var afterDiff);
        var consumedFullFile = afterDiff.TryConsume("full_file", 6, out var afterFullFile);

        consumedDiff.Should().BeTrue();
        consumedFullFile.Should().BeFalse();
        afterFullFile.Should().BeSameAs(afterDiff);
        afterDiff.ConsumedContentTokens.Should().Be(35);
        afterDiff.RemainingContentTokens.Should().Be(5);
        afterDiff.ConsumedSections.Should().Equal(new PromptBudgetSection("diff", 35));
    }

    [Fact]
    public void ConsumeAvailableDrawsOnlyRemainingBudget()
    {
        var budget = PromptBudget.Create(
            modelContextLimitTokens: 50,
            systemPromptTokens: 10,
            groundingTokens: 10,
            responseReserveTokens: 10);

        var updated = budget.ConsumeAvailable("retrieval", 25, out var consumedTokens);

        consumedTokens.Should().Be(20);
        updated.RemainingContentTokens.Should().Be(0);
        updated.ConsumedSections.Should().Equal(new PromptBudgetSection("retrieval", 20));
    }

    [Fact]
    public void CreateRejectsNegativeTokenInputs()
    {
        var create = () => PromptBudget.Create(
            modelContextLimitTokens: -1,
            systemPromptTokens: 0,
            groundingTokens: 0,
            responseReserveTokens: 0);

        create.Should().Throw<ArgumentOutOfRangeException>();
    }
}
