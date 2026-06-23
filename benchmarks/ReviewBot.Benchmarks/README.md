# ReviewBot.Benchmarks

BenchmarkDotNet micro-benchmarks for ReviewBot's hot, non-I/O code paths. Kept out
of `ReviewBot.sln` so `dotnet build` / `dotnet test` at the root don't pick it up;
run it explicitly in Release:

```bash
dotnet run -c Release --project benchmarks/ReviewBot.Benchmarks
```

## Benchmarks

- **`CSharpParserBenchmarks`** — `CSharpRepoSymbolParser.Parse` over ReviewBot's own
  `src/**/*.cs`, the workload of a full retrieval index. `[MemoryDiagnoser]` reports
  allocations alongside time.

## Baseline → optimized (`CSharpRepoSymbolParser`, Apple M4, .NET 10)

| Change | Mean | Allocated |
|---|---:|---:|
| Baseline (interpreted regex) | 75.5 ms | 35.9 MB |
| Source-generated regex (`[GeneratedRegex]`) | 26.9 ms | 35.9 MB |
| + `EnumerateMatches` for the identifier pass | **24.0 ms** | **22.2 MB** |

Net: **~3.1× faster, ~38% fewer allocations**, behavior unchanged (the regex
patterns and the 100 ms ReDoS timeout are preserved).
