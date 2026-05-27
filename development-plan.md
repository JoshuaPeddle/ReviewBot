# ReviewBot Strategic Roadmap (v6)

## What shipped (Phases 1–21)

Phases 1–18 built the core product: webhook ingestion, idempotent queue, full GitHub review posting, Anthropic and OpenAI-compatible LLM adapters, Ollama/local model support, multi-provider grounding (language detection, build runners, test runners), speculative self-critique, incremental review state, and per-repo YAML configuration.

Phase 19 added OpenAI-compatible robustness: structured JSON response format, malformed-response parse-and-repair, per-provider response-format config, and token-usage metrics on every LLM call.

Phase 20 shipped a complete evaluation harness (`tests/ReviewBot.Evals/`): fixture format (fixture.yaml, diff.patch, repo-state/, expected.yaml), rule-based scoring (must_flag, must_not_flag, max comments, review state), multi-fixture aggregation, a `compare` regression command, and a three-fixture quick smoke corpus under `make eval-quick`.

Phase 21 shipped the latency wins: parallel config+metadata fetches on known head SHA, parallel grounding+full-file context, speculative self-critique (starts while agentic context is in flight), mostly-new file skipping (>90% additions ratio skips full-file fetch), and Anthropic prompt caching with `cache_control: ephemeral` on the system prompt (disabled for repair requests, toggleable via config).

Phase 22 (foundation slices) shipped the retrieval scaffolding: `src/ReviewBot.Retrieval/` project with `IDiffSymbolExtractor` and a C#-aware regex extractor, `IRepoIndex` / `IRepoSymbolParser` backed by SQLite with SHA-scoped storage, GlobMatcher for ignore paths, `CSharpLexicalSanitizer` for raw-string/comment stripping shared by both extraction paths, `RetrievalConfig` wired into `ReviewConfig` and repo YAML parsing, and `RepositoryContextSnippet` rendered by `PromptBuilder` before the diff under `## Repository context`. Retrieval defaults to `enabled: false` until the worker integration slice ships.

---

## Context shaping the current priority

GitHub Copilot usage-based billing started June 1, 2026. The window for "competent self-hosted alternative" is now, not September. A v0.3 release needs to work reliably for the most common adoption profile: **a small local model (Qwen 9B, 32K context) on a developer's own hardware, reviewing real production PRs.**

That profile exposes the single largest reliability gap today: **context overflow.** As retrieval context, full-file context, grounding, and diff all accumulate, the prompt silently grows past the model's limit. The model either truncates (missing critical context), errors, or halluccinates. There is no guardrail, no estimation, and no fallback. This gets worse with every improvement we make — retrieval adds more context, full-file fetching adds more context, and the prompt only grows.

Multi-pass chunked review is now a blocker, not a stretch goal. Without it, large PRs on small models degrade unpredictably. With it, any PR can be reviewed correctly regardless of model size — the bot just takes more passes on a large PR.

The right sequencing:

1. **Context-window awareness + token estimation** — foundational, prerequisite to everything. The bot must know how much room it has before building a prompt.
2. **Multi-pass chunked review** — splits PRs that exceed the content budget into chunks and merges results. Unblocks correct behavior on small models.
3. **Retrieval worker integration** — wire the existing retrieval foundation into the worker using context-aware budgets. Retrieval context replaces agentic guesswork.
4. **Observability** — traces, OTel spans, per-review cost. Needed to detect regressions and debug user-reported issues as adoption grows.
5. **WebUI** — last, for non-developer operators.

---

## Phase 22 (current): Context-aware multi-pass retrieval

### Step 1: Context-window awareness and token estimation

#### The problem

The worker assembles the prompt from multiple sources — system prompt, grounding, retrieval snippets, full-file context, diff — with only a rough byte budget controlling size. Bytes are not tokens. A 32K-token Qwen model with a heavy system prompt and grounding might have 20K tokens of headroom; a 200K-token Claude model might have 180K. The worker has no idea which is which, and the byte-to-token ratio varies by language (code is token-dense).

Overflow is silent: the provider either truncates, errors, or produces garbage. There is no warning, no logged evidence, and no retry with a smaller prompt.

#### The design

**Model context registry.** A new `IModelContextRegistry` (single implementation: `ModelContextRegistry`) maps model identifiers to context window sizes. Defaults baked in for common models:

| Pattern | Context tokens |
|---|---|
| `claude-*` | 200 000 |
| `gpt-4*`, `gpt-5*` | 128 000 |
| `qwen*:*b*` | 32 768 |
| `llama3*:8b*`, `*:8b*` | 8 192 |
| `llama3*:70b*`, `*:70b*` | 131 072 |
| `granite*` | 128 000 |
| (fallback) | 8 192 |

Config overrides in `appsettings.json`:

```yaml
ModelContext:
  Limits:
    "qwen2.5:9b-q4_K_M": 32768
    "my-custom-model": 16384
```

Pattern matching is longest-prefix-wins. The registry is injected as a singleton; both LLM adapters (Anthropic, OpenAI-compatible) read it to supply the limit for their current model.

**Token estimation.** A new `IPromptTokenEstimator` with two implementations:

- `AnthropicTokenEstimator` — calls the Anthropic `count_tokens` API synchronously before sending (only when the prompt is large; below a threshold, use the heuristic). The count_tokens call adds latency but is far cheaper than a failed review.
- `HeuristicTokenEstimator` — `Math.Ceiling(charCount / 3.5)` for source code (code is denser than prose), used for OpenAI-compatible providers and as a fast pre-check.

The estimator is applied per prompt section so the worker knows how much each section costs before committing to it.

**Budget-aware prompt assembly.** `ReviewWorker` computes a `PromptBudget` before assembling context:

```
content_budget = model_context_limit
                 - system_prompt_tokens  (estimated once per config)
                 - grounding_tokens      (estimated after grounding runs)
                 - response_reserve      (config, default 4096)
```

This budget is threaded through `FullFileContextAsync`, retrieval context selection, and the diff inclusion logic. Each component draws from the budget and stops when it's exhausted. The diff always takes priority — if the diff alone exceeds the content budget, multi-pass kicks in (see Step 2).

Config:

```yaml
review:
  response_reserve_tokens: 4096   # tokens reserved for model output
```

**Logging.** Every review logs the estimated prompt tokens, content budget, and how each section consumed it at Debug level. Overflow is logged at Warning with the model and PR number.

#### Stop test

`PromptBudget` tests: correct subtraction of fixed sections, budget drawn to zero stops fetching more context. `ModelContextRegistry` tests: known-model lookup, config override, fallback. `HeuristicTokenEstimator` tests: empty string, ASCII, source code samples. Worker integration test: when estimated diff tokens exceed content budget, multi-pass is triggered rather than truncation.

#### Completed foundation slice (May 27, 2026)

Shipped the context-window and heuristic-estimation primitives that unblock budget-aware prompt assembly:

- Added `IModelContextRegistry` / `ModelContextRegistry` with baked-in limits for Claude, GPT-4/5, Qwen `*b`, Llama 8B/70B, Granite, and an 8,192-token fallback. `ModelContext:Limits` appsettings overrides are wired through the API composition root; invalid non-positive limits are ignored.
- Added `IPromptTokenEstimator` / `HeuristicTokenEstimator` using `ceil(charCount / 3.5)`.
- Added immutable `PromptBudget` accounting for model limit, system prompt, grounding, response reserve, consumed sections, and remaining content budget.
- Added Core tests for known-model lookup, config override, fallback, heuristic estimates, fixed-section subtraction, zero clamping, and budget exhaustion.
- Added an API composition test proving the budgeting services resolve from DI.

Corrected assumption discovered while reading the area: `OpenAiLlmOptions` and the configuration docs default to structured JSON responses, but production `appsettings.json` was overriding OpenAI-compatible reviews to `text`. The default appsettings value is now `json_object`; `appsettings.Development.json` still uses `text` for local Ollama compatibility.

Remaining in Step 1: Anthropic `count_tokens`, per-section estimation in the live worker, `response_reserve_tokens` repo config, budget-aware full-file/retrieval/diff assembly, logging, and the worker integration test that triggers multi-pass when the diff exceeds budget.

---

### Step 2: Multi-pass chunked review

#### The problem

A large refactor touches 80 files. The diff is 15K lines. Even with a 200K-token Claude model, this might be borderline; with a 32K Qwen model, it's impossible in one pass. Today the worker silently drops files once `max_files` or byte budgets are hit, meaning large PRs get incomplete reviews with no indication of what was skipped.

Multi-pass fixes this: when the total diff exceeds the content budget, split files into chunks that fit, review each chunk independently with the same system prompt and grounding, and merge all comments into a single `ReviewResult`.

#### The design

**Chunking.** When `EstimatedDiffTokens(allFiles) > content_budget`, the worker enters chunked mode:

1. Sort files by directory prefix (keeps related files together in the same chunk).
2. Greedily pack files into chunks: add a file to the current chunk if it fits within `content_budget * 0.85` (leave headroom for prompt assembly overhead), otherwise start a new chunk.
3. Minimum one file per chunk even if it exceeds budget (avoids infinite loop on pathologically large single files — those get truncated at the existing `max_file_bytes` limit).

**Per-chunk review.** Each chunk is reviewed with identical system prompt and grounding. The `ReviewRequest` carries a `ChunkIndex` and `TotalChunks` annotation; the prompt builder inserts `(reviewing chunk N of M)` into the user turn so the model knows it is seeing a partial view. This matters for comment quality — the model should not claim to see the whole PR when it can't.

**Result merging.** After all chunks complete:

- Comments are deduplicated by `(path, line, kind)`: if two chunks produce overlapping comments (unlikely but possible at chunk boundaries), keep the higher-severity one.
- `ReviewState` is the highest severity across all chunks (`REQUEST_CHANGES` > `COMMENT` > `APPROVE`).
- Self-critique runs once over the merged set, not per chunk (avoids 3× the critique cost).
- Token usage is summed across all LLM calls and reported as a single total.

**Parallel vs sequential chunks.** For cloud providers with high rate limits, chunks can run in parallel (`Task.WhenAll`). For local models where parallelism is harmful (single GPU, no batching), chunks run sequentially. The LLM adapter exposes `bool SupportsParallelRequests` (Anthropic: true, OpenAI-compatible: false by default, configurable).

Config:

```yaml
review:
  chunked_review: true            # default true; set false to disable chunking and fall back to today's truncation
  max_chunks: 10                  # safety cap; if a PR needs more, only the first N*budget worth is reviewed
  chunk_headroom: 0.85            # fraction of content_budget used per chunk
```

**Eval signal.** Add two fixture categories to the eval corpus:

- **Large-PR fixtures**: a PR spanning multiple directories; must_flag entries spread across files in different chunks. Passes only if merging works correctly.
- **Cross-chunk reference fixtures**: a bug that is visible only when two files from different chunks are considered together. These are expected to fail initially and serve as the benchmark for when retrieval (Step 3) closes the gap.

#### Stop tests

Chunking strategy tests: single file, N files fitting in one chunk, N files requiring exactly two chunks, a single file exceeding budget (clamped to one chunk). Merge tests: no duplicates, duplicate resolution prefers higher severity, review state max. Parallel vs sequential dispatch controlled by `SupportsParallelRequests`. Worker integration test: a 5-file PR with a 1-file content budget produces 5 chunks, one comment per chunk, final result has 5 comments merged and one self-critique call.

---

### Step 3: Retrieval worker integration

The retrieval foundation (diff symbol extractor, SQLite repo index, C# parser, config, prompt surface) is already built. This step wires it into the live review path.

**What gets added:**

- `IRetrievalProvider` (new interface): given a `ReviewRequest` + `PromptBudget`, returns a list of `RepositoryContextSnippet` up to the remaining budget. The budget portion reserved for retrieval is `min(retrieval.max_bytes / avg_bytes_per_token, content_budget * 0.3)` — retrieval never consumes more than 30% of content budget.
- `SqliteRetrievalProvider` (first implementation): calls `IDiffSymbolExtractor` on the diff, queries `IRepoIndex` for definitions and top-3 callers per symbol, deduplicates and ranks hits by relevance (definitions first, then callers), converts to snippets within budget.
- Worker integration: when `retrieval.enabled: true`, run `IRetrievalProvider` in parallel with grounding (after token fetch and config), inject snippets into `ReviewRequest.RepositoryContext`, proceed to prompt assembly.
- Incremental indexing: on each review, trigger a background `IRepoIndex.IndexAsync` for the PR's head SHA if that SHA is not already indexed. Use the GitHub compare API to fetch only changed-file paths, re-parse only those paths. The first review of a new SHA blocks until indexing completes; subsequent reviews of the same SHA are instant.
- Eviction: a background `IHostedService` runs nightly, calling `IRepoIndex.DeleteUnusedBeforeAsync(DateTime.UtcNow - TimeSpan.FromDays(30))`.

**What this replaces:**
- Agentic context (`review.agentic_context`) becomes opt-in rather than the default enrichment path. It stays in the worker as a fallback but defaults off once retrieval is stable.
- `review.full_file_max_bytes` is superseded but kept active until retrieval is proven across the eval corpus; the deprecation decision comes after Phase 23 eval data.

**Remaining known limitation:** the C# parser is lexical/regex, not tree-sitter or Roslyn. Retrieval quality will be bounded by this. The tree-sitter migration is a separate follow-on slice, not a blocker for shipping the worker integration.

**Stop tests:** `IRetrievalProvider` unit tests with a mock index. Worker integration test: with `retrieval.enabled: true`, symbols extracted from a synthetic diff are looked up, snippets injected into the request, snippets appear in the rendered prompt. Budget cap: if snippets would exceed the retrieval budget fraction, they are trimmed to fit. SHA-not-indexed triggers `IndexAsync` before review continues.

---

## Phase 23: Observability and review traces

The existing metrics (queue depth, processing rate, LLM duration) diagnose system health. They do not explain *why a specific review was wrong*, which is the question every user bug report starts with.

### Review trace persistence

For every review, write a JSON file under `traces/{repo}/{prNumber}-{deliveryId}.json`:

- Job metadata (delivery ID, owner, repo, PR number, SHA, trigger reason)
- Config snapshot (effective `ReviewConfig`)
- Chunk count and per-chunk summaries (if chunked)
- Full prompt per chunk: system + user (or a hash if `Tracing__IncludePrompts: false` to save disk)
- Raw LLM response per chunk (pre-parse)
- Parsed `ReviewResult` per chunk + merged result
- Self-critique prompt/response if it ran
- Agentic context request + fetched files if applicable
- Retrieval hits per chunk: symbols queried, snippets returned, bytes used
- Final comments posted vs filtered with drop reasons
- Per-stage timings (token fetch, config, grounding, retrieval, LLM per chunk, merge, post)
- Token usage per chunk and total
- Estimated cost (when provider rate is configured)
- Content budget and actual consumption per section

Stored with a size cap (default 500MB, `Tracing__MaxDiskMb`) and TTL cleanup (default 14 days, `Tracing__RetentionDays`) via the existing cleanup service. Off by default in production (`Tracing__Enabled: false`); on by default in dev appsettings. When `IncludePrompts: false`, only the section headers and byte counts are recorded — useful for production deployments handling sensitive code.

Traces are the primary debugging artifact: "user says the bot flagged something weird on PR 42" → hand over the trace JSON.

### OpenTelemetry spans

Wrap major worker stages in `ActivitySource` spans:

```
reviewbot.review
├─ reviewbot.fetch_token
├─ reviewbot.fetch_config
├─ reviewbot.fetch_pr_files
├─ reviewbot.grounding
│  ├─ reviewbot.grounding.tier1_language
│  ├─ reviewbot.grounding.tier2_build
│  └─ reviewbot.grounding.tier3_tests
├─ reviewbot.retrieval
│  ├─ reviewbot.retrieval.extract_symbols
│  ├─ reviewbot.retrieval.index_sha
│  └─ reviewbot.retrieval.lookup
├─ reviewbot.chunk_review (one per chunk)
│  ├─ reviewbot.llm.review
│  └─ reviewbot.llm.self_critique (if ran per-chunk)
├─ reviewbot.merge_chunks
├─ reviewbot.llm.self_critique (merged)
└─ reviewbot.post_review
```

Add OTLP exporter (`OpenTelemetry.Extensions.Hosting` plus `OpenTelemetry.Exporter.OpenTelemetryProtocol`). Self-hosters wire to Jaeger, Tempo, or any OTLP-compatible collector. Document in README with a `docker-compose.yml` Jaeger example.

Span attributes: `review.owner`, `review.repo`, `review.pr_number`, `review.sha`, `review.model`, `review.chunk_index`, `review.total_chunks`, `llm.prompt_tokens`, `llm.completion_tokens`, `retrieval.symbols_queried`, `retrieval.snippets_returned`, `retrieval.bytes_used`.

### Cost surface

When token usage is reported by the provider, compute estimated dollar cost per review using a configurable per-million-token rate:

```yaml
CostRates:
  "claude-opus-4-7":
    InputPer1M: 15.00
    OutputPer1M: 75.00
  "claude-sonnet-4-6":
    InputPer1M: 3.00
    OutputPer1M: 15.00
```

Surface as a `reviewbot.cost.usd_total` counter (labels: provider, model, phase: review/self_critique/retrieval_index) and a field in the trace JSON. For Ollama models, the rate is zero; the counter stays at zero. Cost visibility matters a lot to company adopters evaluating cloud model spend.

---

## Phase 24: WebUI

Defer until Phases 22 and 23 ship. The CLI, logs, and trace JSON are sufficient for development and early adopters.

### Scope for v1

Blazor Server, same .NET 10 stack as the API. Single project, served from the same host. No separate frontend toolchain.

Pages:

- **Reviews list.** Recent reviews across all installations, filterable by repo and status. Click into a review.
- **Review trace detail.** The trace JSON rendered: system prompt, user prompt per chunk (collapsed), raw LLM response (collapsed), parsed comments, posted comments, dropped comments with reasons, per-stage timings, token usage, estimated cost. Multi-pass reviews show a chunk-by-chunk accordion then the merged result.
- **Metrics dashboard.** OTel metrics as charts: reviews per hour, p50/p95 latency, token usage trend, cost trend, chunk count distribution.
- **Config editor.** Pull `.github/review-bot.yml` via GitHub API, render with validation, commit via the GitHub App (opt-in, requires Contents:write).
- **Eval results browser.** Latest eval run, fixture-by-fixture pass/fail, comment-quality scores, diff against previous run.

Single Basic Auth password for the whole UI. No multi-tenant auth in v1 — this is a self-hosted single-tenant tool.

---

## Things still on the list (post-Phase 22)

**One-command Docker bring-up.** `docker-compose.yml` in the repo root: ReviewBot + Ollama + model puller, three env vars to fill in. This is the difference between "adopters say yes" and "adopters say too hard." Priority just behind Phase 22.

**Tree-sitter upgrade for retrieval.** The current C# parser is lexical. Tree-sitter gives correct symbol extraction for C#, and the multi-language path for JS/TS, Go, Rust. Significant integration work (native binary portability on Linux container + Mac + Windows). Build after Phase 22 is stable and measured.

**Severity calibration.** The LLM overuses `error`, which triggers `request_changes_on_error` as a false merge block. Eval harness should report severity distribution per model. System prompt needs a calibration anchor. Add two eval fixture categories: one that should produce exactly one `error`, one that should produce zero `error` despite having real bugs. The distribution across the corpus is the signal.

**Pre-LLM secret scrub.** Fast regex over diff and retrieved files before prompt assembly. Patterns: AWS access keys, GitHub PATs, Stripe keys, private key blocks, hardcoded password shapes. Either redact or abort the review with a clear log line. The agentic-context fetcher already does path-based scrubbing (`.env`, `*.pem`); content-based scrubbing closes the gap when a secret ends up in a `.cs` file.

**Inline `suggestion` blocks.** GitHub renders ` ```suggestion ``` ` as one-click diffs. The prompt mentions this but the model rarely produces them. A focused prompt-engineering pass plus a few eval fixtures grading on "did the model produce an applicable suggestion when the fix is mechanical" would push this substantially.

**A public eval scoreboard.** Run the eval against Claude Opus 4.7, Sonnet 4.6, GPT-5.1, Qwen 9B, Llama 3.1 8B. Publish in the README. "Here's what you get with local Qwen vs cloud Claude, measured" is better marketing than any feature list.

**Smaller model for self-critique.** The critique pass is binary classification per comment. It doesn't need the same model as the review. A cheaper/faster model would make self-critique cost-free for every review. Wire in once Phase 22 stabilizes.

**Conversation continuity.** When a PR author replies "this is intentional, see ADR-042," the bot shouldn't re-flag the same issue next push. Hard: requires tracking comment threads, classifying replies, selective re-review. Defer to v2.

**Multi-tenant / cloud product.** Explicitly deferred. The MIT self-hosted story is the product.

---

## Risks

**Context estimation accuracy.** Heuristic token counting (chars/3.5) can be off by 20–30% for mixed-language diffs with heavy Unicode or whitespace. The Anthropic count_tokens API is accurate but adds a round-trip. If chunk boundaries are calculated from an underestimate, the assembled prompt still overflows — just less often. Status: open after the May 27 foundation slice; the registry, heuristic estimator, and budget accounting now exist, but the worker does not yet enforce them. Mitigation: apply a conservative headroom factor (0.85 per chunk, response_reserve 4096) and log when actual token usage from the response differs more than 15% from the estimate. Tune the heuristic against the eval corpus once token usage reporting is in place.

**Multi-pass quality on cross-chunk bugs.** A bug that requires context from two files in different chunks will not be caught until retrieval injects the missing context. The eval corpus should have 3–5 cross-chunk fixtures to measure how bad this gap actually is. Expect multi-pass alone (without retrieval) to miss these; retrieval's job is to close the gap.

**Retrieval parser quality.** The lexical C# parser misclassifies edge-case syntax and produces false positives on symbol extraction. Symbol lookup hits with wrong matches dilute the retrieval context budget. Measure via eval: if retrieval is on but scores lower than no retrieval, the parser noise is the cause. Switching to tree-sitter or Roslyn is the fix; do it after the measurement confirms the gap.

**Chunked review cost.** A 10-chunk review is 10× the LLM cost. For cloud models at scale, this matters. The `max_chunks` config is the safety cap, but users may not know what value to set. The WebUI cost dashboard (Phase 23) is the feedback loop. Until then, log total token usage and estimated cost at the review completion level so it shows up in standard logs.

**SQLite index contention.** The repo index uses SQLite. Concurrent reviews of the same repo could race on index writes. SQLite with WAL mode handles concurrent reads fine but serializes writes. For the expected deployment scale (single-maintainer self-hosted), this is acceptable. If concurrent review volume grows, the contention will show up as index write latency in OTel spans.

**Anthropic SDK stability.** `Anthropic.SDK` 5.x remains unofficial. Prompt caching and fine-grained cache control are version-sensitive. Pin the version in `Directory.Packages.props` and document. Watch for an official Anthropic .NET SDK; migrate when one ships.
