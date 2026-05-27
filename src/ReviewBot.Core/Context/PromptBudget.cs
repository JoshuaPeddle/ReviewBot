namespace ReviewBot.Core.Context;

public sealed record PromptBudget
{
    private PromptBudget(
        int modelContextLimitTokens,
        int systemPromptTokens,
        int groundingTokens,
        int responseReserveTokens,
        int contentBudgetTokens,
        IReadOnlyList<PromptBudgetSection> consumedSections)
    {
        ModelContextLimitTokens = modelContextLimitTokens;
        SystemPromptTokens = systemPromptTokens;
        GroundingTokens = groundingTokens;
        ResponseReserveTokens = responseReserveTokens;
        ContentBudgetTokens = contentBudgetTokens;
        ConsumedSections = consumedSections;
    }

    public int ModelContextLimitTokens { get; init; }

    public int SystemPromptTokens { get; init; }

    public int GroundingTokens { get; init; }

    public int ResponseReserveTokens { get; init; }

    public int ContentBudgetTokens { get; init; }

    public IReadOnlyList<PromptBudgetSection> ConsumedSections { get; init; }

    public int ConsumedContentTokens => ConsumedSections.Sum(section => section.Tokens);

    public int RemainingContentTokens => Math.Max(0, ContentBudgetTokens - ConsumedContentTokens);

    public static PromptBudget Create(
        int modelContextLimitTokens,
        int systemPromptTokens,
        int groundingTokens,
        int responseReserveTokens)
    {
        ThrowIfNegative(modelContextLimitTokens, nameof(modelContextLimitTokens));
        ThrowIfNegative(systemPromptTokens, nameof(systemPromptTokens));
        ThrowIfNegative(groundingTokens, nameof(groundingTokens));
        ThrowIfNegative(responseReserveTokens, nameof(responseReserveTokens));

        var contentBudgetTokens = Math.Max(
            0,
            modelContextLimitTokens - systemPromptTokens - groundingTokens - responseReserveTokens);

        return new PromptBudget(
            modelContextLimitTokens,
            systemPromptTokens,
            groundingTokens,
            responseReserveTokens,
            contentBudgetTokens,
            []);
    }

    public bool CanConsume(int tokens)
    {
        ThrowIfNegative(tokens, nameof(tokens));

        return tokens <= RemainingContentTokens;
    }

    public bool TryConsume(string sectionName, int tokens, out PromptBudget updated)
    {
        ThrowIfInvalidSectionName(sectionName);
        ThrowIfNegative(tokens, nameof(tokens));

        if (!CanConsume(tokens))
        {
            updated = this;
            return false;
        }

        updated = WithSection(sectionName, tokens);
        return true;
    }

    public PromptBudget ConsumeAvailable(string sectionName, int requestedTokens, out int consumedTokens)
    {
        ThrowIfInvalidSectionName(sectionName);
        ThrowIfNegative(requestedTokens, nameof(requestedTokens));

        consumedTokens = Math.Min(requestedTokens, RemainingContentTokens);
        return consumedTokens == 0 ? this : WithSection(sectionName, consumedTokens);
    }

    private PromptBudget WithSection(string sectionName, int tokens) =>
        this with
        {
            ConsumedSections = ConsumedSections
                .Concat([new PromptBudgetSection(sectionName.Trim(), tokens)])
                .ToArray()
        };

    private static void ThrowIfNegative(int value, string paramName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Token counts cannot be negative.");
        }
    }

    private static void ThrowIfInvalidSectionName(string sectionName)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
        {
            throw new ArgumentException("A prompt budget section name is required.", nameof(sectionName));
        }
    }
}
