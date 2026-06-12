# ReviewBot Roadmap

Forward-looking only. The history of shipped slices lives in [CHANGELOG.md](CHANGELOG.md).

## What this is

ReviewBot is a self-hosted GitHub App that reviews pull requests with any Anthropic or OpenAI-compatible LLM, including local models via Ollama. It targets MIT-licensed self-hosting as the product. Cloud / multi-tenant is explicitly out of scope.

## Strategic context

The wedge is being a competent self-hosted alternative for the common adoption profile: a developer running a strong local model on their own hardware, reviewing real production PRs. The reference profile is **`qwen/qwen3.6-27b` at 72K context** — fits a 24GB NVIDIA card (3090/4090/A5000) or a 32GB+ M-series Mac with unified memory. A 7B-class model remains a documented fallback for tighter machines, but it is not the reference for quality measurement.

The critical reliability gap the small-model profile exposed — silent prompt overflow — is structurally closed (Phase 22 shipped budget-aware prompts, multi-pass chunking, and retrieval; Phase 23 shipped traces, OTel, and cost). The remaining gap to v0.3 is adoption ergonomics and measured proof of quality, not more features.

## v0.3 ship gate

Three conditions, all measured:

1. **Quality**: retrieval lifts F1 by ≥0.05 on a ≥16-fixture corpus, averaged over 3 trials, on the reference local model (`qwen/qwen3.6-27b`, 72K context). Latest measurement on the 11-fixture corpus (June 11): baseline F1 = 0.957, retrieval (bodies, 30-line cap, `both` depth) F1 = 0.952. The corpus saturates at F1 ≈ 1.0 — the gate cannot be answered without more fixtures specifically engineered so the bug requires deep cross-file body context. Body extraction now ships and fires by default; the remaining quality work is corpus design, not retrieval plumbing.
2. **Adoption**: one-command Docker bring-up works end-to-end against a real GitHub App, documented in the README.
3. **Honesty**: README accurately reflects shipped features, with the local-model story front and center.

Until all three are true, do not start new features.

---

## Current phase: harden and measure (Phase 22.5)

### Deliverables, ordered

#### 1. Expand eval corpus and measure with repeated trials

Partial progress on June 11 (see CHANGELOG entry "Body-bearing retrieval, scorer flexibility, corpus expansion"): added 3 fixtures (009 hardcoded-secret, 010 async-race, 011 SQL-injection), bringing corpus to 11. Scorer extended with `additional_locations` so cross-file bugs can be flagged at either cause or effect site. Body extraction works and fires by default. But: corpus still saturates at F1 ≈ 1.0 against the reference model, so the +0.05 ship-gate cannot be answered yet.

Remaining work:

- **Design retrieval-differentiating fixtures (target +5 fixtures)**: bugs that are only spottable when the model can read a method body from another file. Examples: callee contract violation (caller passes a value forbidden by the callee's docstring/preconditions), interface impl drift (a hidden non-edited implementor of the changed interface breaks), private helper invariant (a method assumes its caller has validated input, but the caller no longer does), invalidated cache key (a cache write uses a different key shape than the read). The point is: baseline-only model lacks the body; retrieval-on model has it.
- **Address LM Studio capacity wall on fixture 005**: it timed out at 285s on the retrieval pass even with the 30-line body cap. Either tune per-fixture retrieval budgets further, accept that this model can't review that prompt, or design the prompt assembly to leave more headroom.
- **Clean up stale fixture directories**: `006-cross-chunk-api-contract`, `007-cross-chunk-shared-utility`, `008-cross-chunk-conditional-compilation`, `009-cross-chunk-dependency-injection` exist with `repo-state/` but no `fixture.yaml`. Decide: complete or delete.
- **Repeat trials**: run each retrieval-on / retrieval-off pair 3 times per fixture, report mean F1 ± std-dev. Single-trial measurements can't distinguish a 0.04 effect from noise.
- **Stop test**: `make eval-quick` reports mean F1 ± std-dev for both retrieval modes; the v0.3 ship-gate query is a single command.

#### 2. Promote the phase label on `reviewbot.cost.usd_total`

The original cost spec called for `phase: review | self_critique | retrieval_index` labels (see CHANGELOG "Cost surface slice"); the shipped counter only labels `provider` and `model`. Self-critique on a 10-chunk review is the dominant cost driver and is currently invisible.

- Add `phase` label to `reviewbot.cost.usd_total` and to `LlmTokenUsage` records when they cross adapter boundaries.
- Surface `EstimatedCostUsdByPhase` in `ReviewTrace`.
- Trace JSON shows per-phase cost so operators can decide whether to wire a smaller critique model (deferred item below).
- Stop test: worker test records cost emissions tagged with phase for a review that ran self-critique.

#### 3. Refactor `ReviewWorker.cs` before adding to it

`src/ReviewBot.Api/Workers/ReviewWorker.cs` is 2391 lines. Every Phase 22/23 slice bolted into it: budget math, agentic context, chunk planning and dispatch, retrieval orchestration, trace assembly, span tagging. The next slice will cost more than the refactor does.

- Extract `ChunkOrchestrator` (chunk planning + dispatch + parallel/sequential gating + merge).
- Extract `PromptBudgetPlanner` (model-limit lookup, grounding/system-prompt/retrieval/full-file consumption accounting).
- Extract `TraceBuilder` (assemble `ReviewTrace` from outcomes, timings, agentic data, dropped comments).
- Pure refactor. No behavior change. Validated by existing worker test suite + OTel span test.

#### 4. Adoption ergonomics: docker-compose + README

`docker-compose.yml` brings up ReviewBot + Ollama + model puller with three env vars to fill. README rewrite leads with the local-model adoption story, then surfaces retrieval / chunking / traces / OTel / cost / evals. Today the README still describes the 0.1.0 product.

- Also: `src/ReviewBot.Api/Dockerfile` is missing `COPY ["src/ReviewBot.Retrieval/ReviewBot.Retrieval.csproj", "src/ReviewBot.Retrieval/"]`. Fix at the same time.

#### 5. Decide the agentic-context path

Currently half-deprecated: spec says it becomes opt-in once retrieval is stable, but it still has active code, trace data, and recent bug fixes. Half-deprecated paths produce the most bugs.

- Option A: rip it out. Retrieval covers the use case structurally; remove `review.agentic_context`, the second-pass review code, and `TraceAgenticContext` once trace consumers are warned.
- Option B: commit to it as a documented fallback for cases retrieval can't cover (cross-repo references, runtime-loaded files). Document when it engages and update the cost path so `CompleteRawAsync` reports token usage.
- Decide. Don't leave it as it is.

---

## Open risks

**Multi-pass quality on cross-chunk bugs.** June 11 measurement on the 11-fixture corpus shows retrieval lifts precision by +0.083 (suppresses model false positives) but the corpus saturates at F1 ≈ 1.0, so the +0.05 F1 ship gate cannot be answered without harder fixtures. Mitigation: deliverable #1 ("Design retrieval-differentiating fixtures").

**Silent chunk-skipping.** `max_chunks: 10` on a small model + 80-file PR drops files. The skipped-file note in the merged summary is not a substitute for the operator knowing whether the dropped files contained the bug. Mitigation candidate: either make `max_chunks` overflow a hard error or surface a coverage % in the trace and posted summary. Pick when delivering #4.

**Self-critique on merged set.** Critique runs once over all chunks' comments, but the model that produced each comment only saw its own chunk. Critique can demote correct comments as unsubstantiated. No eval fixture exercises this. Add one as part of deliverable #1.

**`RawLlmResponse` capture has no size guard or opt-out.** `Tracing__IncludePrompts: false` strips prompts but the response side isn't behind a parallel toggle. For operators running against private code, the captured response is the riskiest persisted field. Mitigation: add `IncludeResponses` toggle and per-chunk byte cap. Small slice; consider folding into deliverable #4.

**Context estimation accuracy.** Heuristic (chars/3) for OpenAI-compatible/local; Anthropic uses `count_tokens` above a threshold. Mitigation: keep `response_reserve_tokens` at 4096; tune `chunk_headroom` from eval data once deliverable #1 ships.

**Retrieval cold-start latency.** First retrieval-enabled review of a SHA clones a temp checkout and may full-parse. Delta reviews with an indexed base now copy unchanged symbols (CHANGELOG: changed-path incremental indexing). Remaining exposure: review of a SHA whose base wasn't indexed. Mitigation: measure in Phase 23 traces; revisit persistent index workspaces if visible.

**Retrieval parser quality.** Lexical C# parser produces false positives that dilute the retrieval budget. Symbol-body extraction shipped June 11 (30-line brace-balanced cap on method definitions) and works empirically — snippets are now real method bodies, not 1-line stubs. Remaining lexical-parser exposure: false-positive symbol matches on common names, missed bodies for constructors (no return type), and partial-method edge cases. Mitigation: measure via eval once corpus exposes the gap; consider tree-sitter / Roslyn upgrade then.

**Retrieval index eviction discovery.** Nightly eviction only sweeps cache directories opened by the running process; cannot discover custom `retrieval.index_cache_dir` values that were only used before a restart. Mitigation: persisted cache-directory registry if real deployments rely on many custom caches.

**Anthropic SDK stability.** `Anthropic.SDK` 5.x is unofficial. Prompt caching and fine-grained cache control are version-sensitive. Pin in `Directory.Packages.props`. Migrate when an official .NET SDK ships.

---

## After v0.3 (in rough priority order)

- **Tree-sitter or Roslyn upgrade for retrieval.** Current C# parser is lexical and noisy. Required for accurate symbol extraction and the JS/TS/Go/Rust multi-language path.
- **Severity calibration.** The LLM overuses `error`, which triggers `request_changes_on_error` as a false merge block. Eval should report severity distribution per model; add fixture categories that should produce exactly one `error` and zero `error` despite real bugs.
- **Pre-LLM secret scrub.** Regex over diff and retrieved files before prompt assembly. Either redact or abort. Closes the gap when a secret ends up in a `.cs` file.
- **Inline `suggestion` blocks.** GitHub renders ` ```suggestion ``` ` as one-click diffs. Prompt mentions it but model rarely produces them. Add eval fixtures grading for mechanical fixes.
- **Public eval scoreboard.** Run the eval against Claude Opus 4.7, Sonnet 4.6, GPT-5.1, qwen/qwen3.6-27b (reference), qwen2.5:7b (fallback), Llama 3.1 8B. Publish in README.
- **Smaller model for self-critique.** Critique is binary classification per comment. A cheaper/faster model would make critique cost-free. Wire after deliverable #2 makes per-phase cost visible.
- **Conversation continuity.** When a PR author replies "this is intentional, see ADR-042," don't re-flag next push. Requires comment-thread tracking + reply classification. Defer to v2.
- **Phase 24 — WebUI.** Blazor Server, single Basic Auth password. Pages: reviews list, trace detail, metrics dashboard, config editor, eval results browser. Deferred until v0.3 ships.

---

## Out of scope

**Multi-tenant / cloud product.** The MIT self-hosted story is the product.
