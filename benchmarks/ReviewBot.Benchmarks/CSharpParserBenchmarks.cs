using BenchmarkDotNet.Attributes;
using ReviewBot.Retrieval.Indexing;

namespace ReviewBot.Benchmarks;

// Measures full-repo symbol indexing — the heaviest non-I/O work in the pipeline
// (it runs on every full index). The corpus is ReviewBot's own src/**/*.cs.
[MemoryDiagnoser]
public class CSharpParserBenchmarks
{
    private readonly CSharpRepoSymbolParser parser = new();
    private (string Path, string Content)[] corpus = [];

    [GlobalSetup]
    public void Setup()
    {
        var root = FindRepoRoot();
        var srcDir = Path.Combine(root, "src");
        corpus = Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(p => (Path: Path.GetRelativePath(root, p), Content: File.ReadAllText(p)))
            .ToArray();

        if (corpus.Length == 0)
        {
            throw new InvalidOperationException($"No source files found under {srcDir}");
        }
    }

    [Benchmark(Description = "Parse all src/**/*.cs")]
    public int ParseCorpus()
    {
        var total = 0;
        foreach (var (path, content) in corpus)
        {
            total += parser.Parse(path, content).Count;
        }

        return total;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReviewBot.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (ReviewBot.sln)");
    }
}
