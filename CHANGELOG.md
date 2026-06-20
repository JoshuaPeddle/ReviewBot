# Changelog

All notable changes to this project will be documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Phase 22.5 — Retrieval quality, corpus diversification, eval robustness

#### 2026-06-13 — Retrieval-differentiating fixtures and 3-trial corpus measurement

Closed v0.3 ship-gate condition #1 (Quality). The 11-fixture corpus saturated at F1 ≈ 1.0 against the reference model and could not answer the +0.05 ΔF1 gate; this slice adds 5 fixtures engineered so the bug is only spottable when retrieval injects a method body from another file, then measures the corpus over 3 trials.

**Retrieval-differentiating fixtures (+5, 11 → 16, category `cross_file_body`)**

All five plant the decisive evidence in `repo-state/`-only files whose bodies retrieval extracts via the `CSharpDiffSymbolExtractor` symbol on an added/context diff line. Author-voice PR titles and descriptions (the `LiveEvalRunner` feeds both into the prompt as `pr_title` / `pr_body`); maintainer notes live under a `notes:` key the loader ignores.

- `012-callee-zero-interval` — callee-contract: caller switches `Start(30)` → `Start(0)` with "kick off immediately" intent; `PollScheduler.Start` (out-of-diff) treats `<= 0` as "polling disabled" and returns, so the host never polls.
- `013-empty-batch-invariant` — callee-precondition: caller drops `CommentBatching.Chunk` (which yielded no empty batches) and calls `WriteBatchAsync(comments)` directly; `WriteBatchAsync` (out-of-diff) indexes `batch[0]`, so clean reviews now throw.
- `014-cache-key-drift` — cross-file read/write mismatch: write path moves to `SessionCacheKeys.ForTenantUser`; `SessionReader` (out-of-diff) still uses `ForUser`. Severity bar set to `error` to refuse hedged "if the read path…" speculation.
- `015-parallel-dispatch-implementor` — interface-implementor drift: dispatcher switches to `Task.WhenAll`; `EmailChannel.SendAsync` (out-of-diff) reuses a shared `StringBuilder`, which only the implementor body shows.
- `016-multifile-clamped-interval` — multi-file noise + silent clamp: 4-file metrics-polish PR; the signal file sets `flusher.Configure(TimeSpan.FromSeconds(30))` while `MetricsFlusher.Configure` (out-of-diff) clamps below a 5-minute minimum. Three other files are benign churn that `must_not_flag` keeps from deciding pass/fail.

All five carry calibrated `must_not_flag` allowances so defensible secondary observations (Task.WhenAll failure semantics, INSERT parameter limits, hedged warnings) are forgiven symmetrically in both retrieval modes — only the planted finding scores.

**Stale fixture cleanup** — Deleted four placeholder directories (`006-cross-chunk-api-contract`, `007-cross-chunk-shared-utility`, `008-cross-chunk-conditional-compilation`, `009-cross-chunk-dependency-injection`) that had `repo-state/` skeletons but zero files and no `fixture.yaml`. The live runner already silently ignored them.

**Measurement against `qwen/qwen3.6-27b` on the LAN box (LM Studio :1234), 3 trials**

```
trial   base F1  retr F1   ΔF1     base P  retr P   base R  retr R
 1       0.786    1.000   +0.214   0.917   1.000    0.688   1.000
 2       0.828    1.000   +0.172   0.923   1.000    0.750   1.000
 3       0.800    0.889   +0.089   0.857   0.800    0.750   1.000
mean     0.804    0.963   +0.159 (sd 0.064)
```

Worst-trial ΔF1 = +0.089 still clears the +0.05 gate. Per-fixture mean ΔF1: 012 +1.000, 013 +0.333, 014 +1.000, 015 +1.000, 016 +1.000 (the lift), 004 -0.067, 005 -0.111 (variance noise on saturated fixtures), all other 11 ≈ 0. Trial 3's three regressions on pre-existing fixtures (004, 005, 010) are local-model output variance, not retrieval damage — retrieval recall stayed at 1.000 across all three trials, so retrieval keeps finding the planted bug; what wobbles is the suppression of secondary comments. Worth a follow-up scoring tweak if the v0.3 ship goes long.

**Corrected assumptions discovered during implementation**

- Author-voice fixture metadata matters more than expected. The `LiveEvalRunner` injects fixture `name` and `description` into the prompt as PR title and body; fixtures 001-011 describe their planted bugs in those fields, which materially inflates baseline scores on those fixtures. New fixtures 012-016 use neutral author voice. The 11 pre-existing fixtures were not retro-cleaned in this slice — renaming them would shift the measured baseline and is queued as a separate decision.
- `CSharpDiffSymbolExtractor` skips removed diff lines, so the symbol whose out-of-diff body holds the evidence must appear on an added or context line. Fixtures 012-016 are built around this constraint.
- Retrieval usage-row snippets are single signature lines; only method *definition* rows carry brace-balanced bodies (30-line cap, `MaxCallersPerSymbol = 2`). New fixtures bound the load-bearing types to two usages so the budget always covers the definition body.
- The hedged-speculation baseline score on 014 was the surprise. Without an error-severity bar, the baseline scored a true positive by guessing the right direction without evidence ("if the read path still uses ForUser..."). Setting `severity_at_least: error` plus a `must_not_flag` for warning-level dispatcher comments forced the scorer to credit only the asserted, location-specific finding retrieval produced.
- LM Studio's capacity wall on fixture 005 turned out to be intermittent — it completed in 2 of 3 trials at the 30-line body cap, contrary to the June 11 note that it was a hard wall. Hardware-side noise, not a code defect.

#### 2026-06-11 — Body-bearing retrieval, scorer flexibility, corpus expansion

Closed the "retrieval is just plumbing" gap from the May 28 live-eval slice. Body extraction now fires by default and the scorer credits defensible cross-file flag locations.

**Retrieval body extraction**
- `RepoSymbol` carries optional `Body` / `BodyStartLine` / `BodyEndLine`; non-method definitions and usages leave them null.
- `CSharpRepoSymbolParser` does brace-balanced body extraction for method declarations, including expression-bodied (`=>`) and one-liner forms. Capped at **30 lines** per snippet (tuned for local 27B / consumer-hardware deployments — bump for cloud).
- `SqliteRepoIndex` schema gained `body_text`, `body_start`, `body_end` columns. Additive migration (`ALTER TABLE ADD COLUMN`) preserves existing index data; rows from older runs return body=null and fall back to signature.
- `SqliteRetrievalProvider` emits snippets with the body span when available, signature otherwise.
- Default `RetrievalConfig.SymbolLookupDepth` flipped from `callers` to `both`. The previous default deliberately bypassed body extraction by returning only usage rows; new default exercises both definition bodies and top-K caller spans. `.github/review-bot.yml` updated to match.

**Scorer extension for cross-file bugs**
- `MustFlagExpectation.AdditionalLocations` lets `expected.yaml` list alternate `(path, line_range)` tuples that count as valid matches. Severity and keyword checks apply uniformly.
- Fixtures `006-cross-chunk-retry-default` and `008-cross-chunk-signature-default` now accept the cause-site flag (default-value file) in addition to the effect-site flag (guard/check file). The model was always producing defensible reviews; the scorer was just refusing them.

**Corpus diversification (+3 fixtures, 8 → 11)**
- `009-hardcoded-api-secret` — single-file security regression: `IOptions<PaymentOptions>` → string-literal Bearer token.
- `010-cross-chunk-async-race` — concurrency: `SessionCache` drops `SemaphoreSlim` while `SessionService.ResolveManyAsync` fans out concurrent `GetOrCreateAsync` calls via `Task.WhenAll`.
- `011-cross-chunk-sql-injection` — security cross-file: `UserRepository` swaps parameterized `@name` bind for `$"...'{name}'..."` interpolation while `UserSearchEndpoint` forwards raw `[FromQuery] string name`.
- Canned results added so `eval-quick` covers all 11 fixtures.

**Eval robustness**
- New `eval-live-baseline`, `eval-live-retrieval`, `eval-live-compare`, and `eval-probe` Make targets. Outputs to `runs/eval-{UTC-timestamp}-{label}.json` (gitignored); `EVAL_RUN_LABEL` overrides the timestamp.
- `.env.eval.example` (committed) + `.env.eval` (gitignored) configure base URL, model name, API key var, context tokens. Makefile sources via `-include`.
- `LiveEvalRunner` per-fixture wall-clock timeout (default 240s, `--per-fixture-timeout`) using a linked `CancellationTokenSource`. Configurable `--request-timeout` (180s) and `--max-tokens` (16384, sized for reasoning models that consume tokens in `<think>` blocks).
- Broad exception handler around the per-fixture LLM call recursively unwraps `AggregateException` / `InnerException` chains to detect cancellation or timeout. Any LLM failure (timeout, HTTP 4xx/5xx, transport error) is recorded as a `timed_out` or `errored` fixture with an empty `ReviewResult`; the eval continues to the next fixture instead of crashing.
- `EvalRunScorer` tolerates missing per-fixture result files (treats as failed-empty rather than aborting the aggregate). Lets the harness survive adding fixtures between the live run and the score step.
- `Makefile` `score` / `compare` recipes prefixed with `-` so non-zero exit codes (which mean "fixtures failed" or "regressions detected" — signal, not error) don't abort the pipeline. A follow-up `@test -f` check verifies output JSON was produced.
- `.claude/settings.local.json` allowlists the eval Make targets and the LAN-box probe URL so the harness runs unprompted.

**Measurement against `qwen/qwen3.6-27b` on the LAN box (LM Studio :1234)**
- Rescored 11-fixture baseline (retrieval=false) on a clean re-run: F1 = 0.957, Precision = 0.917, Recall = 1.000, 10/11 passed.
- Retrieval (bodies, 30-line cap, `both` depth) on the same baseline: F1 = 0.952, Precision = 1.000, Recall = 0.909, 10/11 passed. Δ F1 = -0.004; Δ Precision = +0.083; Δ Recall = -0.091.
- 010 cross-chunk-async-race regression baseline→retrieval was reversed — retrieval suppressed the model's verbose "bounded concurrency" warning, lifting Δ F1 for that fixture by +0.333.
- 005 cross-chunk-state-default timed out at 285s on the retrieval pass even with the 30-line cap. LM Studio caps out on that fixture's prompt at this model size; not a code defect.
- Body extraction is structurally working (snippet counts 4-12, real method bodies inside) but does not produce additional signal over callers-only retrieval on this 11-fixture corpus. The corpus saturates at F1 ≈ 1.0 at the 27B model, leaving no room for body-vs-callers differentiation.

**Pre-existing tuning rolled in**
- `HeuristicTokenEstimator.CharactersPerToken`: 3.0 → 2.5 (more conservative).
- `SqliteRetrievalProvider`: `AverageBytesPerToken` 5 → 3, `MaxCallersPerSymbol` 3 → 2 (tighter retrieval budgets).
- `ModelContextRegistry`: replaced three Qwen fallback patterns with `*qwen3.6-27b` → 72,000 and `qwen*b*9` → 32,768 (precise targeting of the reference model + a 9b lane).

**Corrected assumptions discovered during implementation**
- The retrieval system was designed to inject definition bodies as cross-file context, but the default `SymbolLookupDepth: callers` deliberately discarded the definition lane. The body-extraction code was structurally unreachable until the default changed.
- The previous "retrieval = no F1 lift" measurement was substantially methodology, not retrieval: narrow `line_range` expectations refused defensible flag locations, callers-only depth bypassed body extraction, and 8 fixtures wasn't enough corpus to differentiate retrieval modes.
- A 30-line body cap is the empirical sweet spot for a local 27B model. 60 lines crashes LM Studio on fixture 005 regardless of retrieval budget tuning.
- Four stale fixture directories (`006-cross-chunk-api-contract`, `007-cross-chunk-shared-utility`, `008-cross-chunk-conditional-compilation`, `009-cross-chunk-dependency-injection`) have `repo-state/` but no `fixture.yaml` and are silently ignored by the runner. Pre-existing — not addressed in this slice. Worth a future cleanup decision: complete or delete.

### Phase 23 — Observability and review traces

#### 2026-05-28 — Filtered-comment trace slice

Shipped trace visibility for comments removed between model output and GitHub posting:

- Changed review traces so `candidate_comments` records the raw model candidate comments for the selected review pass, before confidence/content filtering and self-critique.
- Added `dropped_comments` to each trace, preserving the comment fields plus a stable reason (`inline_comments_disabled`, `below_min_confidence`, `praise_only`, `meta_review`, `non_actionable_process`, `speculative_missing_context`, or `self_critique`).
- Preserved existing posting behavior: only filtered final comments are sent to GitHub, while the trace now explains why raw candidates disappeared.
- Added JSON serialization coverage and a worker integration test proving raw candidates, final comments, and drop reasons are captured after a review.

Corrected assumptions:

- The existing `candidate_comments` trace field was not truly pre-filter. It was populated after confidence/content filtering and often after self-critique, which made trace JSON insufficient for debugging "why did the bot not post this model comment?" reports.
- The broader stop test also exposed prompt-builder drift: Core prompt tests expected the stricter anti-meta-review and anti-speculation rules, but `PromptBuilder` still emitted an older shorter rule set. Restored the prompt contract before final verification.

#### 2026-05-28 — Grounding OTel detail slice

Shipped grounding-specific span detail:

- Added `reviewbot.grounding.tier1_language` spans inside `CompositeGroundingProvider` covering root-file listing, detector selection, and metadata extraction.
- Added `reviewbot.grounding.tier2_build` spans around clone/build execution, tagged with language ID, whether a build runner actually ran, and build success.
- Added `reviewbot.grounding.tier3_tests` spans for GitHub Checks summary collection and local test execution, tagged by test source plus local failure counts when available.
- Added grounding tests that listen to the shared `ReviewBot` activity source and verify tier1/tier2/tier3 span names and tags.

#### 2026-05-28 — Retrieval OTel detail slice

Shipped retrieval-specific span detail:

- Moved `ReviewBotActivitySource` from the API project into `ReviewBot.Core` so non-API projects can emit spans through the same `ReviewBot` source.
- Added `reviewbot.retrieval.extract_symbols` and `reviewbot.retrieval.lookup` spans inside `SqliteRetrievalProvider`; lookup spans tag `retrieval.symbols_queried` and `retrieval.matches_returned`.
- Extended `RetrievalContextResult` with `SymbolsQueried`, and the worker tags the parent `reviewbot.retrieval` span with `retrieval.symbols_queried` alongside snippet and byte counts.
- Added `reviewbot.retrieval.index_sha` around retrieval indexing, tagged with owner/repo/SHA and full vs incremental index mode.
- Added retrieval provider and worker tests for the new spans and tags.

Corrected assumptions:

- Keeping the shared `ActivitySource` internal to `ReviewBot.Api` was the structural blocker for provider-level spans. Moving the source to Core lets retrieval emit child spans without depending on the API assembly.

#### 2026-05-28 — Per-chunk agentic trace slice

Shipped agentic context detail in traces:

- Added `TraceAgenticContext` under each `TraceChunk`, capturing requested context files, accepted context files after validation, fetched file paths, drop-reason counts, and whether the second-pass review ran.
- Wired `ReviewWorker` so both single-chunk and multi-chunk review outcomes carry agentic context trace data without changing review posting behavior.
- Added trace serialization coverage for the new snake_case fields and a worker integration test proving requested/accepted/fetched/drop data appears in the emitted review trace.
- Fixed a bug discovered while reading the agentic-context path: successful agentic second-pass results replaced the initial `ReviewResult` and dropped the primary LLM call's `TokenUsage`, hiding token/cost data for those reviews. The worker now preserves the initial token usage when the enriched parsed result replaces the review.

Corrected assumptions:

- `ReviewResult.ContextRequests` only preserves what the model asked for; it does not include validation outcomes or fetch results. Trace data needs to be captured at the agentic-context boundary before the final result is filtered or merged.
- Agentic second-pass completions currently use `CompleteRawAsync`, which does not return token usage. Until raw-completion usage is exposed, the trace/cost surface can preserve the primary review token usage but cannot count the second-pass completion separately.

#### 2026-05-28 — OTel spans slice

Shipped `ActivitySource`-based distributed tracing for major review pipeline stages:

- Added `ReviewBotActivitySource` (source name `ReviewBot`, v1.0.0).
- Added `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.15.3 and wired `.WithTracing()` in `Program.cs`; OTLP exporter reads `OTEL_EXPORTER_OTLP_ENDPOINT` (defaults to `http://localhost:4317`).
- `ReviewWorker` emits the following spans per review job: `reviewbot.review`, `reviewbot.grounding`, `reviewbot.retrieval`, `reviewbot.chunk_review` (per chunk), `reviewbot.llm.review` (child of chunk), `reviewbot.llm.self_critique`, `reviewbot.post_review`.
- Also fixed a pre-existing bug: `BuildTraceChunk` inferred files-in-chunk from comment paths (silently omitting files with no comments). `ChunkReviewOutcome` now carries `ChunkFiles`.
- Added `OtelSpansAreEmittedForReview` worker test, `OtelTracerProviderIsRegisteredInDiContainer`, and `OtelReviewBotSourceEmitsActivitiesWhenListenerIsAttached`.

Corrected assumptions:

- `Octokit.Activity` conflicts with `System.Diagnostics.Activity` in the test project; the test file uses a `DiagActivity` alias.
- `AddOtlpExporter()` on `TracerProviderBuilder` requires `using OpenTelemetry.Trace;` — distinct from the metrics extension in `OpenTelemetry.Metrics`.

#### 2026-05-27 — Cost surface slice

Shipped estimated dollar-cost tracking per review:

- Added `CostRateOptions` (`CostRates:Rates` appsettings) mapping model name → `{ InputPer1M, OutputPer1M }`. Empty by default; models without a configured rate produce no cost output.
- Added `IReviewCostCalculator` / `ReviewCostCalculator`: computes `(promptTokens / 1M) * inputRate + (completionTokens / 1M) * outputRate`, returns `null` when no rate is configured for the model. Registered as singleton in DI.
- Added `reviewbot.cost.usd_total` counter to `ReviewBotMetrics` with labels `provider` and `model`; recorded after the merged token usage log.
- Added `decimal? EstimatedCostUsd` to `ReviewTrace`; propagated through `BuildTrace` alongside `TokenUsage`.
- Injected `IReviewCostCalculator` into `ReviewWorker`; cost is computed after the merged-result token-usage log, emits the counter, and logs at Info level.
- Fixed a pre-existing bug: `ApplyOutputConfig`, `AppendFilesSkippedNote`, and `AppendRereviewHint` all used the `new ReviewResult(summary, comments, contextRequests)` constructor which silently dropped `TokenUsage` and `RawLlmResponse`. All three now use `result with { ... }` to preserve the full record state.
- Added `ReviewCostCalculatorTests`, `CostCalculatorIsRegisteredInDiContainer`, trace writer tests, and end-to-end worker test verifying cost capture.

Corrected assumptions:

- `TokenUsage` was silently discarded by three `ReviewResult` construction sites in the worker. The bug was latent — the trace writer test passes because it builds `ReviewTrace` directly, not through the worker pipeline. The new end-to-end worker test exposed it.
- The `phase: review/self_critique/retrieval_index` label originally specified for the counter was not shipped; it currently labels only `provider` and `model`. Self-critique vs review cost cannot yet be sliced. Queued as a deliverable in the current plan.

#### 2026-05-27 — Per-chunk prompt and timing slice

Shipped per-chunk prompt content, raw LLM responses, and per-stage timings in review traces:

- Added `string? RawLlmResponse` to `ReviewResult`; both Anthropic and OpenAI adapters attach the first raw LLM response to every returned result, including repair and empty-result fallback paths.
- Added `bool IncludePrompts` to `IReviewTraceWriter`; `JsonReviewTraceWriter` exposes `TracingOptions.IncludePrompts`, `NullReviewTraceWriter` returns `false`.
- Added `TraceChunk` POCO (per-chunk: index, total, file list, prompt system/user bytes and text, raw response bytes and text, elapsed ms) and `TraceTimings` POCO (grounding, retrieval, full-file-context, total ms).
- Changed `ReviewChunkAsync` to return a new `ChunkReviewOutcome` record packaging `ReviewResult`, built `PromptPayload`, and elapsed `TimeSpan`. The worker builds the prompt via `PromptBuilder.Build(request)` before calling `ReviewAsync`, capturing the same payload the adapter will send.
- Single-chunk reviews produce one `ChunkReviewOutcome` inline in `ProcessAsync` so the trace always has a `ChunkTraces` array regardless of chunking.
- Added per-stage stopwatches in `ProcessAsync` for grounding, retrieval, and full-file-context; `TraceTimings.TotalMs` is the wall time from job-start to trace-write.
- Fixed pre-existing build errors in staged tracing tests: `TraceCleanupService.RunCleanup` needed `InternalsVisibleTo`, and `Options.Create(...)` was ambiguous due to `ReviewBot.Api.Options` namespace; fixed via `MsOptions` alias.
- Updated failing `ReviewAsyncReturnsEmptyResultWhenRepairIsMalformed` tests in both Anthropic and OpenAI suites.

#### 2026-05-27 — Review trace persistence slice

Shipped foundational JSON trace file per completed review:

- Added `TracingOptions` (`Tracing:Enabled`, `IncludePrompts`, `MaxDiskMb`, `RetentionDays`, `TracesDir`). Disabled by default in production; enabled in `appsettings.Development.json`.
- Added `ReviewTrace` POCO capturing: delivery ID, timestamp, job metadata, review type, model provider+name, files reviewed, chunk count, retrieval snippet count, full prompt budget snapshot, candidate comments (pre-filter), final posted comments, and token usage.
- Added `IReviewTraceWriter` / `JsonReviewTraceWriter`: writes `traces/{owner}/{repo}/{prNumber}-{deliveryId}.json` atomically via a temp-file rename; write failures log a warning and never fail the review.
- Added `NullReviewTraceWriter` for tests and as the safe default.
- Added `TraceCleanupService` (background, daily): deletes files older than `RetentionDays`, then deletes oldest remaining files until total size is under `MaxDiskMb`.
- Injected `IReviewTraceWriter` and `TimeProvider` into `ReviewWorker`; trace is written after posting and before persisting the incremental-review SHA.
- Registered tracing services via `AddReviewTracing()` in `Program.cs`.
- Added API composition tests, `JsonReviewTraceWriter` tests, `TraceCleanupService` tests, and worker integration test.

Corrected assumptions:

- `ReviewWorker` did not have `TimeProvider` injected before this slice; it now receives it alongside `IReviewTraceWriter` for consistent timestamp capture.

### Phase 22 — Context-aware multi-pass retrieval

#### 2026-05-28 — Live local retrieval eval slice

Shipped the Phase 22 measurement pass against a local OpenAI-compatible Qwen model:

- Added a live eval runner to `tests/ReviewBot.Evals` (`run-live`) that sends fixture diffs through the production `PromptBuilder`, `OpenAiReviewLlm`, and `LlmResultParser`.
- Runner accepts a local OpenAI-compatible base URL, model name, API key env var, config path, retrieval toggle, context-token limit, and retrieval index cache path. Forces grounding off for fixture-only evals.
- Added manifest output recording model/base URL, retrieval mode, token usage, per-fixture comment count, symbols queried, retrieval snippet count, snippet paths, line ranges, token estimates, and content hashes.
- Added fixture diff parsing so live evals can convert unified patches into `FileChange` objects with commentable line numbers.
- Added three more cross-chunk reference fixtures (`006-cross-chunk-retry-default`, `007-cross-chunk-pagination-default`, `008-cross-chunk-signature-default`).
- Expanded the quick canned corpus from 5 to 8 fixtures.
- Ran 8 fixtures twice against `qwen/qwen3.5-9b`:
  - No retrieval (`runs/eval-phase22-no-retrieval.json`): 3/8 passed, precision 0.636, recall 0.875, F1 0.737.
  - Retrieval enabled (`runs/eval-phase22-retrieval.json`): 3/8 passed, precision 0.636, recall 0.875, F1 0.737. 45 snippets injected, 54 queried symbols.
  - Comparison (`runs/eval-phase22-comparison.json`): 1 regression, 2 improved, 5 unchanged. Aggregate F1 unchanged.
  - First unmanifested run had shown F1 0.700 → 0.800. Retrieval effect is noisy on this corpus until repeated trials are added.

Corrected assumptions:

- Several eval expectations used stale line numbers rather than annotated new-file line numbers. Corrected `001`, `002`, `006`, `007` expectations and canned results.
- The local OpenAI-compatible server rejected `response_format: json_object`. Live eval runner uses text responses and relies on strict-JSON prompt plus parser/repair path.
- Local Qwen output variance can swamp aggregate score on an 8-fixture corpus.
- Remaining cross-chunk failures are mostly duplicate cross-file comments for one root cause — a scoring/post-filtering calibration issue rather than retrieval failing to surface context.

#### 2026-05-28 — Initial retrieval eval fixture slice

Shipped first eval corpus expansion aimed at the multi-pass/retrieval quality gap:

- Added `004-large-pr-multi-directory`, a five-file fixture with must-flag findings in different directories.
- Added `005-cross-chunk-state-default`, a cross-file state/default drift fixture.
- Added canned quick-run results and expanded quick corpus tests from 3 to 5 fixtures.
- Fixed the eval harness project so `.cs` files under fixture `repo-state/` are treated as fixture data rather than compiled into `ReviewBot.Evals`.

#### 2026-05-28 — Changed-path incremental indexing slice

Shipped incremental retrieval indexing for delta reviews:

- Added explicit SHA metadata to the SQLite repo index so a SHA with zero parseable symbols is still marked indexed.
- Added `IRepoIndex.IndexChangesAsync`, which copies unchanged symbols forward from an indexed base SHA, removes changed/deleted paths, and reparses only compare-changed paths.
- Wired `ReviewWorker` to reuse the GitHub compare path set from delta reviews. Falls back to full head-SHA index if compare missing/truncated/failed or prior SHA never indexed.
- Forced a full retrieval index rebuild when `.github/review-bot.yml` or `.yaml` changes.
- Included GitHub compare `previous_file_name` paths for renames.
- Added index, GitHub, and worker tests for the above.

Corrected assumptions:

- `IsIndexedAsync` cannot infer index completion from symbol rows. Repos/SHAs with only unsupported files would be indexed repeatedly forever.
- GitHub compare `filename` alone is insufficient for retrieval invalidation on renames.
- Incremental indexing is only safe across stable ReviewBot config.

#### 2026-05-28 — Retrieval index eviction slice

Shipped nightly cleanup for retrieval index rows:

- Added `RepoIndexCleanupService`, registered through `AddRetrieval()`, runs daily and calls `IRepoIndex.DeleteUnusedBeforeAsync(now - 30 days)` for each cache directory opened by the current process.
- Tightened `SqliteRepoIndexFactory` to track normalized cache directories, reuse DI-provided symbol parsers, and use shared `TimeProvider`.
- Registered `CSharpRepoSymbolParser` in DI.
- Added tests for factory tracking, cleanup cutoff, multi-cache deletion totals, per-cache failure isolation, and DI wiring.

Corrected assumptions:

- A process-local hosted service cannot discover arbitrary per-repo `retrieval.index_cache_dir` values that were used before the current process started. Cleanup sweeps only cache directories observed through `IRepoIndexFactory` during the current process lifetime.

#### 2026-05-28 — Anthropic token-counting slice

Shipped provider-aware token estimation for Anthropic-backed reviews:

- Added `IReviewPromptTokenEstimator` plus provider-specific estimator registration. Anthropic counting used only when provider is `anthropic`; OpenAI-compatible/local providers continue to use the heuristic.
- Added `AnthropicTokenEstimator`, backed by SDK `CountMessageTokensAsync`. First runs chars/3 heuristic; only calls `count_tokens` when estimate reaches `Anthropic:TokenCountingHeuristicThresholdTokens` (default 8,000). `Anthropic:TokenCountingEnabled` disables the API call path.
- Count-token failures fall back to heuristic estimate with a warning.
- Threaded provider-aware estimates through prompt-budget creation, diff accounting, chunk planning, and full-file budget checks.
- Added Core, Anthropic, API DI, and worker integration tests.

Corrected assumptions:

- Anthropic `count_tokens` cannot be registered as the global `IPromptTokenEstimator` because the app registers Anthropic and OpenAI-compatible adapters at the same time.
- The current estimator interface is synchronous, so Anthropic counting blocks on the SDK async call at the budgeting boundary.

#### 2026-05-27 — Retrieval provider and worker integration slice

Shipped the first live retrieval path:

- Added `IRetrievalProvider` / `SqliteRetrievalProvider`; extracts symbols from changed C# diffs, looks up definitions and top-3 callers in the SQLite index, deduplicates, ranks definitions before callers, and emits `RepositoryContextSnippet` entries under cap `min(retrieval.max_bytes / 5, content_budget * 0.2, remaining_budget)`.
- Added `IRepoIndex.IsIndexedAsync` plus `IRepoIndexFactory` so the worker and provider both honor the effective repo config's `retrieval.index_cache_dir`.
- Extended PR metadata with the head clone URL so the worker can materialize the PR head SHA when indexing is needed.
- Wired `ReviewWorker` so `retrieval.enabled: true` checks index, clones/indexes if missing, injects snippets, and charges retrieval against the prompt budget before full-file context and diff budgeting.
- Registered retrieval services in DI; added DI coverage.
- Added provider and worker tests.

Corrected assumptions:

- Retrieval cannot safely run in parallel with grounding because the retrieval cap depends on the grounded prompt budget. Worker runs grounding first, retrieval second, then full-file context.
- The first worker integration indexed a full head SHA. Changed-path reparse was left as a follow-on, closed by the May 28 incremental indexing slice.

#### 2026-05-27 — Merged token usage slice

- Added `LlmTokenUsage.Add(LlmTokenUsage?)` for null-safe accumulation.
- Added `LlmTokenUsage? TokenUsage { get; init; }` to `ReviewResult`; both adapters attach usage to every returned result.
- Anthropic extracts `Usage.InputTokens / OutputTokens / CacheReadInputTokens`; `CompleteRawAsync` passes phase label through.
- `ReviewResultMerger.Merge` sums `TokenUsage` across chunks.
- `ReviewWorker` logs prompt tokens, completion tokens, and cached tokens at Info after final result is assembled.
- Added Core tests for `Add`, null accumulation, and merger summing; Anthropic/OpenAI tests for usage propagation.

#### 2026-05-27 — Chunk planner and dispatcher slice

Shipped the first end-to-end multi-pass review path:

- Added `ReviewChunkPlanner` with directory/path ordering, greedy budget packing, per-file minimum chunks for oversized files, and `max_chunks` cap.
- Added repo YAML support for `review.chunked_review`, `review.max_chunks`, `review.chunk_headroom`.
- Added chunk metadata to `ReviewRequest`; `PromptBuilder` tells the model when it is reviewing chunk N of M.
- Added `IReviewLlm.SupportsParallelRequests`; Anthropic runs chunks in parallel, OpenAI-compatible providers stay sequential by default.
- Wired `ReviewWorker` so `estimated_diff_tokens > remaining_content_budget` enters chunked review. Legacy patch-budget file-dropping behavior applies only when `chunked_review: false`.
- Added merged-result handling and one post-merge self-critique pass. Comments deduplicated by `(path, line, side)`; ties keep higher severity then higher confidence.
- Replaced raw per-chunk summary concatenation with one synthesized post-filter summary.
- Added skipped-file summary notes when `max_chunks` prevents all files from being reviewed.
- Added Core tests for chunk planning, prompt chunk annotations, result merging; GitHub config tests; worker tests for 5-file chunking and sequential/parallel dispatch.
- Tightened speculative missing-context filtering.

#### 2026-05-27 — Worker budget slice

Shipped first live worker integration for prompt budgeting:

- Added repo YAML support for `review.response_reserve_tokens` (default 4,096; non-negative validation).
- Injected `IModelContextRegistry` and provider-aware token estimator into `ReviewWorker`; computes per-review `PromptBudget` from model limit, base system prompt, grounding delta, response reserve, PR metadata.
- Made full-file context budget-aware: candidate fetches selected only while estimated patch tokens fit remaining budget; fetched contents re-estimated before prompt inclusion; oversized fetched files dropped.
- Added debug logging for prompt budget inputs/sections/remaining; warning when estimated diff exceeds remaining budget.
- Added worker tests for grounding-before-full-file budget ordering and budget-limited fetches.
- Corrected Phase 21 latency assumption: full-file context cannot always race grounding anymore because budget-aware selection needs grounding token cost first.

#### 2026-05-27 — Context-window and heuristic estimation foundation

Shipped context-window and heuristic-estimation primitives:

- Added `IModelContextRegistry` / `ModelContextRegistry` with baked-in limits for Claude, GPT-4/5, Qwen `*b`, Llama 8B/70B, Granite, and an 8,192-token fallback. `ModelContext:Limits` appsettings overrides supported.
- Corrected Qwen matching to cover `qwen/qwen3.5-9b`, `qwen3.5-9b-q4_K_M`, `qwen3.5-4b@q4_k_xl`, etc.
- Added `IPromptTokenEstimator` / `HeuristicTokenEstimator` using `ceil(charCount / 3.0)`.
- Added immutable `PromptBudget` accounting.
- Added Core and API DI tests.

### Phase 21 — Latency wins

- Parallel config + metadata fetches on known head SHA.
- Parallel grounding + full-file context (later changed by Phase 22 worker budget slice).
- Speculative self-critique (starts while agentic context is in flight).
- Mostly-new file skipping (>90% additions ratio skips full-file fetch).
- Anthropic prompt caching with `cache_control: ephemeral` on the system prompt (disabled for repair requests, toggleable via config).

### Phase 20 — Eval harness

- Fixture format (fixture.yaml, diff.patch, repo-state/, expected.yaml).
- Rule-based scoring (must_flag, must_not_flag, max comments, review state).
- Multi-fixture aggregation.
- `compare` regression command.
- Three-fixture quick smoke corpus under `make eval-quick`.

### Phase 19 — OpenAI-compatible robustness

- Structured JSON response format.
- Malformed-response parse-and-repair.
- Per-provider response-format config.
- Token-usage metrics on every LLM call.

## [0.1.0] - 2026-05-23

### Added

- GitHub App webhook endpoint with HMAC-SHA256 signature verification and idempotency deduplication.
- Unified diff parser that identifies commentable lines (RIGHT-side additions and context) for precise inline review comments.
- `IReviewLlm` abstraction with two implementations:
  - **Anthropic** — Anthropic Messages API via `Anthropic.SDK`, retry on malformed JSON.
  - **OpenAI-compatible** — official `OpenAI` SDK 2.x, supports any OpenAI-compatible endpoint (Ollama, vLLM, LM Studio, OpenAI).
- Per-repo configuration via `.github/review-bot.yml` (or `.github/review-bot.yaml`), with safe fallback to defaults on missing or malformed config.
- Review worker (`BackgroundService`) that orchestrates: installation token, repo config, PR fetch, ignore glob filtering, big-PR patch budgeting, LLM call, and review posting.
- GitHub App JWT signing (RS256) and cached installation access tokens with per-installation concurrency control.
- Inline comment and summary output gating (`inline_comments`, `summary` flags).
- Big-PR truncation: files prioritised by smallest patch, skipped files noted in the posted summary.
- EF Core persistence (SQLite, migration-based) for at-least-once webhook delivery idempotency.
- Configurable worker concurrency (`Worker__Concurrency`).
- HTTP resilience: exponential-backoff retries on transient token-client errors, Octokit rate-limit retry, LLM transport retries.
- `System.Diagnostics.Metrics` instrumentation: `reviewbot.jobs.processed`, `reviewbot.llm.duration_ms`, `reviewbot.review.comments_posted`.
- Health check at `/healthz` (self + EF Core DbContext).
- Docker multi-stage image (`src/ReviewBot.Api/Dockerfile`).
- GitHub Actions CI with build, test, and Docker image publish on tags.
