using FluentAssertions;
using ReviewBot.Core.Domain;
using ReviewBot.Retrieval.Symbols;

namespace ReviewBot.Retrieval.Tests.Symbols;

public sealed class CSharpDiffSymbolExtractorTests
{
    [Fact]
    public void ExtractFindsSymbolsFromAddedAndContextLines()
    {
        var file = ChangedFile(
            """
            @@ -1,8 +1,11 @@
             using ReviewBot.Core.Domain;
             public sealed class ReviewWorker
             {
            -    private readonly object oldValue;
            +    private readonly IReviewLlm reviewLlm;
            +    public Task<ReviewResult> ProcessAsync(ReviewRequest request, CancellationToken ct)
            +    {
            +        if (request.Config.Retrieval.Enabled)
            +            return reviewLlm.ReviewAsync(request, ct);
            +        return Task.FromResult(new ReviewResult("ok", []));
            +    }
             }
            """);

        var result = new CSharpDiffSymbolExtractor().Extract([file]);

        result.Should().ContainSingle();
        result[0].Path.Should().Be("src/ReviewBot.Api/Workers/ReviewWorker.cs");
        result[0].Symbols.Should().Contain(new DiffSymbol("ReviewBot.Core.Domain", DiffSymbolKind.Import, 1));
        result[0].Symbols.Should().Contain(new DiffSymbol("ReviewWorker", DiffSymbolKind.Type, 2));
        result[0].Symbols.Should().Contain(new DiffSymbol("IReviewLlm", DiffSymbolKind.Type, 4));
        result[0].Symbols.Should().Contain(new DiffSymbol("ProcessAsync", DiffSymbolKind.Method, 5));
        result[0].Symbols.Should().Contain(new DiffSymbol("ReviewRequest", DiffSymbolKind.Type, 5));
        result[0].Symbols.Should().Contain(new DiffSymbol("Config", DiffSymbolKind.Field, 7));
        result[0].Symbols.Should().Contain(new DiffSymbol("Retrieval", DiffSymbolKind.Field, 7));
        result[0].Symbols.Should().Contain(new DiffSymbol("ReviewAsync", DiffSymbolKind.Method, 8));
        result[0].Symbols.Should().Contain(new DiffSymbol("FromResult", DiffSymbolKind.Method, 9));
        result[0].Symbols.Should().NotContain(symbol => symbol.Name == "oldValue");
    }

    [Fact]
    public void ExtractIgnoresStringsCommentsDeletedLinesAndUnsupportedFiles()
    {
        var csharpFile = ChangedFile(
            """"
            @@ -10,8 +10,9 @@ public void Run()
            -    var removed = new RemovedType();
            +    var literal = "ReviewResult ProcessAsync IReviewLlm";
            +    var raw = """
            +        RawStringType.RawStringMethod();
            +        """;
            +    // HiddenType.HiddenMethod();
            +    var current = VisibleType.Create();
            +    /*
            +       BlockCommentType.Ignored();
            +    */
            """");

        var markdownFile = new FileChange(
            "docs/review.md",
            """
            @@ -1 +1 @@
            +# ReviewResult
            """,
            new HashSet<int>(),
            AdditionsCount: 1,
            DeletionsCount: 0,
            FileChangeStatus.Modified);

        var result = new CSharpDiffSymbolExtractor().Extract([csharpFile, markdownFile]);

        result.Should().ContainSingle();
        result[0].Symbols.Should().Contain(new DiffSymbol("VisibleType", DiffSymbolKind.Type, 15));
        result[0].Symbols.Should().Contain(new DiffSymbol("Create", DiffSymbolKind.Method, 15));
        result[0].Symbols.Select(symbol => symbol.Name).Should().NotContain([
            "RemovedType",
            "ReviewResult",
            "ProcessAsync",
            "IReviewLlm",
            "RawStringType",
            "RawStringMethod",
            "HiddenType",
            "HiddenMethod",
            "BlockCommentType",
            "Ignored"
        ]);
    }

    [Fact]
    public void ExtractDeduplicatesSymbolsAtFirstLineSeen()
    {
        var file = ChangedFile(
            """
            @@ -20,4 +20,5 @@ public void Run()
            +    var first = cache.GetValue(request.Id);
            +    var second = cache.GetValue(request.ParentId);
            +    var third = cache.SetValue(request.ParentId);
            """);

        var result = new CSharpDiffSymbolExtractor().Extract([file]);

        result.Should().ContainSingle();
        result[0].Symbols.Should().Contain(new DiffSymbol("GetValue", DiffSymbolKind.Method, 20));
        result[0].Symbols.Should().Contain(new DiffSymbol("SetValue", DiffSymbolKind.Method, 22));
        result[0].Symbols.Should().Contain(new DiffSymbol("ParentId", DiffSymbolKind.Field, 21));
        result[0].Symbols.Count(symbol => symbol.Name == "GetValue").Should().Be(1);
        result[0].Symbols.Count(symbol => symbol.Name == "ParentId").Should().Be(1);
    }

    [Fact]
    public void ExtractSkipsRemovedFiles()
    {
        var file = ChangedFile(
            """
            @@ -1,3 +0,0 @@
            -public sealed class DeletedType
            -{
            -}
            """,
            FileChangeStatus.Removed);

        var result = new CSharpDiffSymbolExtractor().Extract([file]);

        result.Should().BeEmpty();
    }

    private static FileChange ChangedFile(string patch, FileChangeStatus status = FileChangeStatus.Modified) =>
        new(
            "src/ReviewBot.Api/Workers/ReviewWorker.cs",
            patch,
            new HashSet<int>(),
            AdditionsCount: 1,
            DeletionsCount: 1,
            status);
}
