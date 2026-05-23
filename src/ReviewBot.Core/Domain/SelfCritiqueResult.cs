namespace ReviewBot.Core.Domain;

public sealed record SelfCritiqueResult(IReadOnlyList<int> RetainedIndices, string Rationale);
