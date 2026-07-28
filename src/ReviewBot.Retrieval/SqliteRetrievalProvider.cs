using ReviewBot.Core.Context;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Otel;
using ReviewBot.Retrieval.Indexing;
using ReviewBot.Retrieval.Symbols;

namespace ReviewBot.Retrieval;

public sealed class SqliteRetrievalProvider : IRetrievalProvider
{
    private const double RetrievalContentBudgetFraction = 0.2d;
    private const double AverageBytesPerToken = 3d;
    private const int MaxCallersPerSymbol = 2;

    private readonly IRepoIndexFactory indexFactory;
    private readonly IDiffSymbolExtractor symbolExtractor;
    private readonly IPromptTokenEstimator tokenEstimator;

    public SqliteRetrievalProvider(
        IRepoIndexFactory indexFactory,
        IDiffSymbolExtractor symbolExtractor,
        IPromptTokenEstimator tokenEstimator)
    {
        this.indexFactory = indexFactory ?? throw new ArgumentNullException(nameof(indexFactory));
        this.symbolExtractor = symbolExtractor ?? throw new ArgumentNullException(nameof(symbolExtractor));
        this.tokenEstimator = tokenEstimator ?? throw new ArgumentNullException(nameof(tokenEstimator));
    }

    public async Task<RetrievalContextResult> GetContextAsync(
        string owner,
        string repo,
        ReviewRequest request,
        PromptBudget budget,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(budget);

        if (!request.Config.Retrieval.Enabled || budget.RemainingContentTokens == 0)
        {
            return new RetrievalContextResult([], budget);
        }

        var tokenLimit = CalculateRetrievalTokenLimit(request.Config.Retrieval, budget);
        if (tokenLimit <= 0)
        {
            return new RetrievalContextResult([], budget);
        }

        var index = indexFactory.Create(request.Config.Retrieval.IndexCacheDir);
        var key = new RepoIndexKey(owner, repo, request.HeadSha);
        var lookup = await LookupRankedSymbolsAsync(index, key, request, ct).ConfigureAwait(false);
        if (lookup.Symbols.Count == 0)
        {
            return new RetrievalContextResult([], budget, lookup.SymbolsQueried);
        }

        var snippets = new List<RepositoryContextSnippet>();
        var updated = budget;
        var remainingRetrievalTokens = tokenLimit;
        foreach (var symbol in lookup.Symbols)
        {
            // Prefer the brace-balanced body for method definitions; fall back to the
            // signature line for other kinds (types, fields) and usages where no body exists.
            var hasBody = symbol.Body is not null && symbol.BodyStartLine.HasValue && symbol.BodyEndLine.HasValue;
            var content = hasBody ? symbol.Body! : (symbol.Signature ?? symbol.Name);
            var startLine = hasBody ? symbol.BodyStartLine!.Value : symbol.Line;
            var endLine = hasBody ? symbol.BodyEndLine!.Value : symbol.Line;
            var tokens = tokenEstimator.EstimateTokens(content);
            if (tokens == 0)
            {
                continue;
            }

            if (tokens > remainingRetrievalTokens)
            {
                content = TrimToTokenBudget(content, remainingRetrievalTokens);
                tokens = tokenEstimator.EstimateTokens(content);
            }

            if (tokens == 0 || !updated.TryConsume("retrieval", tokens, out var afterRetrieval))
            {
                break;
            }

            snippets.Add(new RepositoryContextSnippet(
                symbol.Path,
                startLine,
                endLine,
                content));
            updated = afterRetrieval;
            remainingRetrievalTokens -= tokens;

            if (remainingRetrievalTokens <= 0)
            {
                break;
            }
        }

        return new RetrievalContextResult(snippets, updated, lookup.SymbolsQueried);
    }

    private static int CalculateRetrievalTokenLimit(RetrievalConfig config, PromptBudget budget)
    {
        var maxBytesAsTokens = (int)Math.Ceiling(config.MaxBytes / AverageBytesPerToken);
        var budgetFractionTokens = (int)Math.Floor(budget.ContentBudgetTokens * RetrievalContentBudgetFraction);
        return Math.Min(Math.Min(maxBytesAsTokens, budgetFractionTokens), budget.RemainingContentTokens);
    }

    private async Task<RankedSymbolLookup> LookupRankedSymbolsAsync(
        IRepoIndex index,
        RepoIndexKey key,
        ReviewRequest request,
        CancellationToken ct)
    {
        var results = new List<RankedRepoSymbol>();
        var seen = new HashSet<(string Name, DiffSymbolKind Kind)>();
        var queries = new List<DiffSymbol>();

        using (var extractActivity = ReviewBotActivitySource.Instance.StartActivity("reviewbot.retrieval.extract_symbols"))
        {
            foreach (var fileSymbols in symbolExtractor.Extract(request.Files))
            {
                foreach (var diffSymbol in fileSymbols.Symbols)
                {
                    if (seen.Add((diffSymbol.Name, diffSymbol.Kind)))
                    {
                        queries.Add(diffSymbol);
                    }
                }
            }

            extractActivity?.SetTag("retrieval.symbols_extracted", queries.Count);
        }

        var symbolsQueried = 0;
        using (var lookupActivity = ReviewBotActivitySource.Instance.StartActivity("reviewbot.retrieval.lookup"))
        {
            var depth = request.Config.Retrieval.SymbolLookupDepth;
            IReadOnlyList<DiffSymbol> pending = queries;
            var maxHops = Math.Max(1, request.Config.Retrieval.MaxHops);

            for (var hop = 1; hop <= maxHops && pending.Count > 0; hop++)
            {
                var hopStart = results.Count;
                foreach (var diffSymbol in pending)
                {
                    var matches = await index.FindAsync(key, diffSymbol.Name, MapKind(diffSymbol.Kind), ct)
                        .ConfigureAwait(false);
                    AddMatches(results, matches, depth, hop);
                }

                symbolsQueried += pending.Count;
                pending = hop < maxHops
                    ? NextHopQueries(results, hopStart, seen)
                    : [];
            }

            lookupActivity?.SetTag("retrieval.symbols_queried", symbolsQueried);
            lookupActivity?.SetTag("retrieval.matches_returned", results.Count);
            lookupActivity?.SetTag("retrieval.max_hops", maxHops);
        }

        var symbols = results
            .GroupBy(item => (item.Symbol.Path, item.Symbol.Line, item.Symbol.Signature), item => item)
            .Select(group => group.OrderBy(item => item.Rank).First())
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Symbol.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Symbol.Line)
            .Select(item => item.Symbol)
            .ToArray();

        return new RankedSymbolLookup(symbols, symbolsQueried);
    }

    /// <summary>
    /// The symbols that the definitions found on this hop themselves refer to.
    /// </summary>
    /// <remarks>
    /// Only definition bodies are followed. A usage match is a single source line with no
    /// body, so following it would mostly re-query the enclosing file's noise. Anything
    /// already queried on an earlier hop is skipped via the shared <paramref name="seen"/>
    /// set, which also stops two mutually recursive methods looping.
    /// </remarks>
    private IReadOnlyList<DiffSymbol> NextHopQueries(
        List<RankedRepoSymbol> results,
        int fromIndex,
        HashSet<(string Name, DiffSymbolKind Kind)> seen)
    {
        var next = new List<DiffSymbol>();
        for (var i = fromIndex; i < results.Count; i++)
        {
            var symbol = results[i].Symbol;
            if (symbol.Role != RepoSymbolRole.Definition || string.IsNullOrWhiteSpace(symbol.Body))
            {
                continue;
            }

            foreach (var referenced in symbolExtractor.ExtractFromSource(symbol.Body!))
            {
                if (seen.Add((referenced.Name, referenced.Kind)))
                {
                    next.Add(referenced);
                }
            }
        }

        return next;
    }

    private static void AddMatches(
        List<RankedRepoSymbol> results,
        IReadOnlyList<RepoSymbol> matches,
        string depth,
        int hop)
    {
        // Rank orders what survives the token budget. Later hops rank strictly worse than
        // anything the diff named directly, so a second-hop snippet can only ever consume
        // budget that a first-hop one did not want.
        var definitionRank = (hop - 1) * 2;
        var usageRank = definitionRank + 1;

        if (depth is RetrievalConfig.DefinitionsDepth or RetrievalConfig.BothDepth)
        {
            results.AddRange(matches
                .Where(symbol => symbol.Role == RepoSymbolRole.Definition)
                .Select(symbol => new RankedRepoSymbol(symbol, definitionRank)));
        }

        if (depth is RetrievalConfig.CallersDepth or RetrievalConfig.BothDepth)
        {
            results.AddRange(matches
                .Where(symbol => symbol.Role == RepoSymbolRole.Usage)
                .Take(MaxCallersPerSymbol)
                .Select(symbol => new RankedRepoSymbol(symbol, usageRank)));
        }
    }

    private static RepoSymbolKind MapKind(DiffSymbolKind kind) => kind switch
    {
        DiffSymbolKind.Type => RepoSymbolKind.Type,
        DiffSymbolKind.Method => RepoSymbolKind.Method,
        DiffSymbolKind.Field => RepoSymbolKind.Field,
        DiffSymbolKind.Import => RepoSymbolKind.Import,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown diff symbol kind.")
    };

    private static string TrimToTokenBudget(string content, int tokenBudget)
    {
        if (tokenBudget <= 0)
        {
            return string.Empty;
        }

        var maxCharacters = Math.Max(0, (int)Math.Floor(tokenBudget * AverageBytesPerToken));
        if (content.Length <= maxCharacters)
        {
            return content;
        }

        return content[..maxCharacters];
    }

    private sealed record RankedRepoSymbol(RepoSymbol Symbol, int Rank);

    private sealed record RankedSymbolLookup(IReadOnlyList<RepoSymbol> Symbols, int SymbolsQueried);
}
