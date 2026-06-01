using System.Diagnostics;
using FluentAssertions;
using ReviewBot.Core.Context;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Otel;
using ReviewBot.Retrieval.Indexing;
using ReviewBot.Retrieval.Symbols;

namespace ReviewBot.Retrieval.Tests;

public sealed class SqliteRetrievalProviderTests
{
    [Fact]
    public async Task GetContextAsyncReturnsDefinitionsThenTopThreeCallersForDiffSymbols()
    {
        var index = new FakeRepoIndex();
        index.Results[("GetAsync", RepoSymbolKind.Method)] =
        [
            new RepoSymbol("GetAsync", RepoSymbolKind.Method, RepoSymbolRole.Usage, "src/Caller1.cs", 10, "repository.GetAsync(id);"),
            new RepoSymbol("GetAsync", RepoSymbolKind.Method, RepoSymbolRole.Definition, "src/IUsers.cs", 4, "Task<User?> GetAsync(int id);"),
            new RepoSymbol("GetAsync", RepoSymbolKind.Method, RepoSymbolRole.Usage, "src/Caller2.cs", 20, "await users.GetAsync(id);"),
            new RepoSymbol("GetAsync", RepoSymbolKind.Method, RepoSymbolRole.Usage, "src/Caller3.cs", 30, "return repo.GetAsync(id);"),
            new RepoSymbol("GetAsync", RepoSymbolKind.Method, RepoSymbolRole.Usage, "src/Caller4.cs", 40, "extra.GetAsync(id);")
        ];
        var provider = new SqliteRetrievalProvider(
            new FakeRepoIndexFactory(index),
            new CSharpDiffSymbolExtractor(),
            new HeuristicTokenEstimator());
        var request = CreateRequest(
            """
            @@ -1,2 +1,3 @@
             public Task<User?> FindAsync(int id)
            +    => repository.GetAsync(id);
            """,
            ReviewConfig.Default with
            {
                Retrieval = ReviewConfig.Default.Retrieval with
                {
                    Enabled = true,
                    SymbolLookupDepth = RetrievalConfig.BothDepth
                }
            });
        var budget = PromptBudget.Create(1_000, 10, 0, 100);

        var result = await provider.GetContextAsync("octo", "reviewbot", request, budget);

        result.Snippets.Select(snippet => (snippet.Path, snippet.StartLine, snippet.Content))
            .Should()
            .Equal(
                ("src/IUsers.cs", 4, "Task<User?> GetAsync(int id);"),
                ("src/Caller1.cs", 10, "repository.GetAsync(id);"),
                ("src/Caller2.cs", 20, "await users.GetAsync(id);"),
                ("src/Caller3.cs", 30, "return repo.GetAsync(id);"));
        result.Snippets.Should().NotContain(snippet => snippet.Path == "src/Caller4.cs");
    }

    [Fact]
    public async Task GetContextAsyncCapsSnippetsToThirtyPercentOfContentBudget()
    {
        var index = new FakeRepoIndex();
        index.Results[("GetAsync", RepoSymbolKind.Method)] =
        [
            new RepoSymbol("GetAsync", RepoSymbolKind.Method, RepoSymbolRole.Definition, "src/IUsers.cs", 4, new string('a', 120))
        ];
        var provider = new SqliteRetrievalProvider(
            new FakeRepoIndexFactory(index),
            new CSharpDiffSymbolExtractor(),
            new HeuristicTokenEstimator());
        var request = CreateRequest(
            """
            @@ -1,2 +1,3 @@
             public Task<User?> FindAsync(int id)
            +    => repository.GetAsync(id);
            """,
            ReviewConfig.Default with
            {
                Retrieval = ReviewConfig.Default.Retrieval with
                {
                    Enabled = true,
                    MaxBytes = 1_000,
                    SymbolLookupDepth = RetrievalConfig.DefinitionsDepth
                }
            });
        var budget = PromptBudget.Create(500, 0, 0, 400);

        var result = await provider.GetContextAsync("octo", "reviewbot", request, budget);

        result.Snippets.Should().ContainSingle();
        result.Snippets[0].Content.Should().HaveLength(90);
        result.Budget.ConsumedSections.Should().Contain(new PromptBudgetSection("retrieval", 30));
    }

    [Fact]
    public async Task GetContextAsyncEmitsRetrievalLookupSpansWithSymbolCounts()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ReviewBotActivitySource.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (activities)
                {
                    activities.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        var index = new FakeRepoIndex();
        index.Results[("GetAsync", RepoSymbolKind.Method)] =
        [
            new RepoSymbol("GetAsync", RepoSymbolKind.Method, RepoSymbolRole.Definition, "src/IUsers.cs", 4, "Task<User?> GetAsync(int id);")
        ];
        var provider = new SqliteRetrievalProvider(
            new FakeRepoIndexFactory(index),
            new CSharpDiffSymbolExtractor(),
            new HeuristicTokenEstimator());
        var request = CreateRequest(
            """
            @@ -0,0 +1,2 @@
            +    => repository.GetAsync(id);
            +    => backup.GetAsync(id);
            """,
            ReviewConfig.Default with
            {
                Retrieval = ReviewConfig.Default.Retrieval with
                {
                    Enabled = true,
                    SymbolLookupDepth = RetrievalConfig.DefinitionsDepth
                }
            });
        var budget = PromptBudget.Create(1_000, 10, 0, 100);

        var result = await provider.GetContextAsync("octo", "reviewbot", request, budget);

        result.SymbolsQueried.Should().Be(1);
        List<Activity> snapshot;
        lock (activities)
        {
            snapshot = [..activities];
        }

        snapshot.Should().Contain(activity => activity.OperationName == "reviewbot.retrieval.extract_symbols");
        var lookupActivity = snapshot.Should()
            .ContainSingle(activity => activity.OperationName == "reviewbot.retrieval.lookup")
            .Subject;
        lookupActivity.GetTagItem("retrieval.symbols_queried").Should().Be(1);
        lookupActivity.GetTagItem("retrieval.matches_returned").Should().Be(1);
    }

    private static ReviewRequest CreateRequest(string patch, ReviewConfig config) =>
        new(
            "PR",
            "",
            "base",
            "head",
            [new FileChange("src/App.cs", patch, new HashSet<int> { 2 }, 1, 0, FileChangeStatus.Modified)],
            config);

    private sealed class FakeRepoIndexFactory(IRepoIndex index) : IRepoIndexFactory
    {
        public IRepoIndex Create(string indexCacheDir) => index;

        public IReadOnlyList<string> GetKnownCacheDirectories() => ["/tmp/reviewbot-test-index"];
    }

    private sealed class FakeRepoIndex : IRepoIndex
    {
        public Dictionary<(string Name, RepoSymbolKind? Kind), IReadOnlyList<RepoSymbol>> Results { get; } = [];

        public Task IndexAsync(RepoIndexRequest request, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task IndexChangesAsync(
            RepoIndexRequest request,
            RepoIndexKey baseKey,
            IReadOnlyCollection<string> changedPaths,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> IsIndexedAsync(RepoIndexKey key, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<RepoSymbol>> FindAsync(
            RepoIndexKey key,
            string name,
            RepoSymbolKind? kind = null,
            CancellationToken ct = default) =>
            Task.FromResult(Results.GetValueOrDefault((name, kind), []));

        public Task<int> DeleteUnusedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
            Task.FromResult(0);
    }
}
