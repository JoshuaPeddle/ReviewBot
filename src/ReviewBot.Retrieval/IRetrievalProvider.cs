using ReviewBot.Core.Context;
using ReviewBot.Core.Domain;

namespace ReviewBot.Retrieval;

public interface IRetrievalProvider
{
    Task<RetrievalContextResult> GetContextAsync(
        string owner,
        string repo,
        ReviewRequest request,
        PromptBudget budget,
        CancellationToken ct = default);
}

public sealed record RetrievalContextResult(
    IReadOnlyList<RepositoryContextSnippet> Snippets,
    PromptBudget Budget,
    int SymbolsQueried = 0);
