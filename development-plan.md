# ReviewBot Strategic Roadmap (v5)

This document picks up where `development-plan.md` left off. Phases 1 to 18 shipped. Phase 19 (OpenAI-compatible robustness) is drafted and stays as planned. Beyond that, the focus shifts from feature breadth to two things at once: a baseline we can measure against (so improvements stop being eyeball judgments), and the smart context management that makes the bot punch above its model size.

## Context that shaped this plan

GitHub Copilot moves to usage-based billing on June 1, 2026. Code review will start metering tokens *and* consuming GitHub Actions minutes simultaneously. Teams running Copilot code review at scale are going to be looking for alternatives almost immediately, and the timing matters: a competent v0.3 ReviewBot release with one-command Docker bring-up is more valuable in June than a polished v1.0 in September.

Quality on a 7-PR sample is solid but unmeasured. Every future quality change (prompt tweaks, retrieval, self-critique tuning) is a guess until there is a scored corpus to run against. This makes the eval harness the single highest-leverage next phase, ahead of any further reviewer-side changes.

The product is "ReviewBot self-hosted, MIT, bring-your-own-LLM." The codebase already handles cloud (Anthropic / OpenAI-compatible) and local (Ollama) cleanly. Smart context management has to work both ways: a 9B local model needs aggressive context trimming and selective retrieval; a 200K context window cloud model wants different tradeoffs (less retrieval pressure, more concern about token cost). Keep the retrieval interface model-agnostic and let model-specific tuning live in adapters.

## Phase order (next 90 days)

1. **Phase 19 as drafted** (Steps 51-52). Already specced in the existing plan. Ship it first. Parse repair, response-format config, and token-usage metrics are foundational for everything below: the eval harness needs the parse failure counter, the latency work needs token-usage numbers, and prompt caching in Phase 21 builds on the response-format scaffolding.

2. **Phase 20: Eval harness.** Build before further quality work. Without it, Phase 22's retrieval changes are pure faith.

3. **Phase 21: Latency reductions.** Mostly pipeline-level, not model-level. Cheap wins that pay for themselves immediately on every review.

4. **Phase 22: Retrieval-based context.** The big quality+efficiency lever. Replaces the agentic-context guesswork with deterministic symbol lookup.

5. **Phase 23: Observability and review traces.** A debug aid that turns into a WebUI feature later.

6. **Phase 24: WebUI.** Last, not first. The CLI and `/healthz` are enough for development; the UI is for adopters.

A loose target: Phase 19 in one to two weeks, Phase 20 two weeks, Phase 21 one week, Phase 22 three to four weeks. That puts a credible v0.3 release in early-to-mid July, with the WebUI as a v0.4 in August. Aggressive but doable as a single-maintainer side project if you stay scoped.

---

## Phase 20: Evaluation harness

### The problem

Seven PRs hand-reviewed is enough to confirm the bot works. It is not enough to detect regressions, compare models, justify a prompt change, or claim the retrieval work is actually helping. Every future quality decision needs a fixed corpus and a scoring rubric, or it's a vibes-based engineering project.

### The approach

A fixture-driven test harness, separate from the existing xUnit suite, that runs the full review pipeline against canned PRs and scores the output two ways: rule-based (did the bot flag the planted issue?) and judge-based (an LLM rates each comment on factuality, actionability, and conciseness).

Three kinds of fixtures, all in `tests/ReviewBot.Evals/Fixtures/`:

- **Planted-bug PRs.** You write the bug, you know the answer. The four-issue diff from your demo is the prototype: one true vulnerability (signature validator returns secret state), one correctness regression (line range check), one debatable choice (channel full mode), one boundary-case widening (`> 0` to `>= 0`). Aim for 10 to 15 of these covering: security boundary leaks, correctness regressions, concurrency bugs, error-handling removal, resource leaks, off-by-one, null handling, and a few "looks suspicious but is fine" negative cases.

- **Real PR snapshots.** Pick 10 to 15 merged PRs from your own repos and the company repos you can use, freeze them at a specific SHA pair (base, head), and hand-author the expected findings. Mix the easy wins (clear bugs that any reviewer would catch) with subtle ones (an issue that needed retrieval-augmented context to spot).

- **Clean PRs.** 5 to 10 PRs that should produce zero or near-zero comments. These guard against regression toward over-eager review. Just as important as the bug fixtures.

### Fixture format

Each fixture is a directory:

```
Fixtures/
  001-webhook-signature-leak/
    fixture.yaml              # metadata
    diff.patch                # unified diff
    repo-state/               # checked-in subset of the repo at base SHA
      src/...
    expected.yaml             # findings the bot should produce
```

`fixture.yaml`:

```yaml
name: Webhook signature validator leaks secret state
category: security_boundary
difficulty: high     # how subtle the issue is
description: |
  A malicious change replaces "return false" on a malformed signature
  header with "return string.IsNullOrWhiteSpace(secret)", leaking
  whether a secret is configured. Should be flagged as security error.
```

`expected.yaml`:

```yaml
must_flag:
  - path: src/ReviewBot.Api/Webhooks/WebhookSignatureValidator.cs
    line_range: [20, 20]
    severity_at_least: warning
    topic: leaks_configuration_state
    # The bot's comment body must mention the underlying concept;
    # don't pin exact wording. A keyword list is fine.
    must_mention_any: ["leak", "trust boundary", "secret", "configuration", "information disclosure"]

must_not_flag:
  - path: src/ReviewBot.Core/Jobs/ChannelReviewJobQueue.cs
    reason: "channel full mode change is debatable, not wrong"
    severity_above: warning   # info-level is fine

max_total_comments: 5
expected_review_state: REQUEST_CHANGES   # given request_changes_on_error: true
```

The schema is deliberately loose. Real LLM reviewers don't produce identical comments across runs, so the rubric grades against concepts and severity bands, not exact text. The `must_mention_any` keyword list is good enough for v0.

### Scoring

Two scoring passes per fixture, both run after the bot produces a `ReviewResult`:

1. **Rule-based.** For each `must_flag` entry, did the bot post a comment within `line_range` at or above the severity floor, with the comment body containing at least one keyword? For each `must_not_flag`, did the bot stay quiet (or below the severity ceiling)? Compute per-fixture precision, recall, and F1, then aggregate.

2. **Judge-based.** An LLM-as-judge pass (use the strongest cloud model available, never the same model being evaluated) rates each comment the bot produced on four axes, each 1-5:
    - Factuality: is the bug real?
    - Specificity: does the comment point to the actual problem?
    - Actionability: is the fix direction clear?
    - Conciseness: free of diff-restating, no padding, no "could throw" hand-waving?

   Same prompt every time, store the judge prompt in the harness so it's versioned. Aggregate to a per-comment mean and per-fixture mean.

The judge is the noisier of the two but catches things rule-based scoring misses, like a comment that flags the right line but with a confused explanation.

### Runner

A new console project, `tests/ReviewBot.Evals/`. CLI:

```
dotnet run --project tests/ReviewBot.Evals -- \
  --model openai:qwen3.5:9b-q4 \
  --base-url http://localhost:11434/v1 \
  --judge anthropic:claude-opus-4-7 \
  --fixtures tests/ReviewBot.Evals/Fixtures \
  --out runs/2026-05-25.json
```

Internally it does what the worker does (build `ReviewRequest`, run through `IReviewLlm`, apply self-critique, etc.) but with the GitHub side mocked out. Output is a JSON file per run; a separate `eval compare runs/A.json runs/B.json` command prints a diff table (which fixtures regressed, which improved). That diff is the thing you look at after every prompt change.

Plus a `make eval-quick` target that runs a 3-fixture smoke set in under a minute, so you don't break the dev loop.

### Calibration first, then everything else

The first useful output of the harness is a baseline score across (a) your local 9B model, (b) Claude Opus 4.7, (c) GPT-5.1, with the current prompt. That score is the regression floor. Every subsequent change has to either improve it or be explicitly accepted as a quality-neutral change for some other reason (cost, latency).

Side effect: this baseline is great marketing copy. "On our 30-fixture eval, ReviewBot with Claude Opus catches 87 percent of planted bugs at 92 percent precision; with local Qwen 9B it catches 71 percent at 84 percent precision." Put it in the README.

### Stretch goals (post v0)

- Mutation testing-style fixture generation: take a clean repo, programmatically introduce known bug patterns (drop a null check, flip a boundary, swap `&&` for `||`), generate fixtures automatically. Cheap way to scale the corpus past 50 fixtures.
- Public OSS PR mining: scrape commits that say "fix bug" or revert recent changes, freeze them at the buggy SHA, run the bot, score whether it surfaces the issue that was fixed. License the corpus carefully.

---

## Phase 21: Latency reductions

### The problem

Per your own assessment, latency is the felt pain. The 9B local model is the dominant cost on each review, but the pipeline around it has multiple sequential stages that can be parallelized, plus a few that can be skipped when the PR is small.

### The cheap wins (do these first)

**Parallelize the pipeline.** Today `ReviewWorker.ProcessAsync` runs sequentially: token fetch, metadata fetch, config fetch, file fetch, grounding, LLM. Several of these depend only on the head SHA. The token fetch must come first (everything else needs the token), but after that:

- Repo config fetch, PR metadata fetch, and grounding (Tier 1 / 2 / 3) can run concurrently.
- File fetch must wait for config (needs `max_files`, ignore globs).
- The LLM call must wait for files and grounding.

`Task.WhenAll` after the token fetch, holding the joined task until file fetch needs the config. Expected savings: 1 to 3 seconds per review depending on grounding tier.

**Anthropic prompt caching.** Anthropic supports prompt caching where unchanged prompt prefixes are cached server-side and re-sent at a discount. The system prompt for a review is identical across all PRs in the same repo (focus, instructions, schema), and the grounding section is identical for the duration of a SHA. Mark the system prompt + grounding block as `cache_control: ephemeral` on the request. First review of a SHA pays full price, subsequent reviews (re-runs, self-critique passes, agentic-context second passes) hit the cache. Self-critique on a single review can save 50 to 70 percent of system-prompt tokens because the system prompt is reused. Worth the small adapter change.

**Skip self-critique when it can't help.** Today self-critique runs whenever `review.self_critique: true` and any comment exists. If every comment is high-confidence already, the critique pass has nothing to do (high-confidence comments are retained unconditionally). Short-circuit: if `candidateComments.All(c => c.Confidence == Confidence.High)`, skip the critique entirely. Same final result, one fewer LLM call.

**Skip full-file context when the file is mostly new.** `review.full_file_max_bytes` currently fetches small modified files in full. But if `f.AdditionsCount / (f.AdditionsCount + f.DeletionsCount) > 0.9`, the diff already contains essentially the whole file. Fetching it is wasted bytes.

**Stream-and-fork the LLM call for self-critique on large PRs.** Not actually streaming the response (the JSON has to be complete before parsing), but: kick off the self-critique LLM call as soon as the first pass returns, in parallel with the agentic-context branch decision. Today these are sequential.

### The harder wins (defer to after Phase 22)

**Skip the prompt entirely on near-empty PRs.** If the entire diff is one file, fewer than 20 changed lines, and contains only formatting changes (whitespace, brace style), short-circuit to "no comments" without an LLM call. Risk: false negatives on the rare interesting one-liner. Mitigate by requiring a heuristic match (only whitespace tokens changed). Saves the entire review cost on noise-PRs.

**Smaller specialized model for self-critique.** The critique pass is a binary classification task per comment (keep or drop). It doesn't need the same model as the review pass. A cheaper/faster model would let you self-critique on every review without latency cost. Wire in once Phase 22 stabilizes the prompt structure.

### Measurement

Every latency change goes through the eval harness's run-time records, not just a stopwatch on one PR. The harness should emit per-stage timings for each fixture. Phase 19's token metrics complete the picture. A latency change that improves p50 by 10 percent but doubles p99 isn't a win.

---

## Phase 22: Retrieval-based context

### The problem

The current context strategy is: diff plus optional small whole files plus optional model-requested files (agentic). This works for changes that are self-contained. It breaks down when the change references something defined elsewhere: a new method that overrides a base class, a field added to a type used 30 places, a refactor that touches the interface but not the implementations. The model can't see the relevant context, the comment is either missed or hallucinated.

Agentic context partially addresses this, but the model has to *guess* what files it needs after seeing the diff, then we make a second LLM round-trip. That's slow and brittle: the 9B model frequently asks for files that don't exist or aren't relevant.

### The approach

Replace agentic context with deterministic, structure-aware retrieval. The reviewer doesn't have to ask for files; the pipeline reads the diff, extracts the symbols it touches, looks them up against an index of the repo, and includes the relevant context automatically.

Two-stage rollout: v0 uses pure symbol-graph retrieval with no embeddings (cheap, deterministic, works for typed languages). v1 adds embedding-based retrieval for natural-language matches and untyped languages.

### v0: Symbol-graph retrieval (start here)

The pieces:

**Symbol extraction from the diff.** For each changed file in the diff, identify the identifiers that appear in added or context lines: type names, method names, field references. Tree-sitter is the standard tool, multi-language, no compiler required. There's a managed wrapper for .NET (TreeSitterSharp or a similar binding), or you can shell out to tree-sitter CLI. Output: a list of (identifier, kind) tuples per file, where `kind` is one of `type`, `method`, `field`, `import`.

**Repo symbol index.** A per-SHA cache mapping identifier names to their definitions and usages elsewhere in the repo. Walk the repo at the PR head, tree-sitter every file in scope (skip ignored paths), record `(symbol, file, line, kind, signature)`. Stored as a SQLite table for selfhost simplicity (no extra service). Keyed by SHA, evicted after N days of unuse.

**Lookup.** For each symbol in the diff, query the index:

- The symbol's definition (if not in the changed file itself).
- The top 3 callers (for methods) or referencers (for types/fields).

**Context selection.** Each lookup returns a (file, line range) hit. Deduplicate to a list of "include this hunk of this file." Cap the total at `retrieval.max_bytes` (default 100KB or so). Concatenate before the diff in the prompt, under a `## Repository context` heading.

The agentic-context fetcher stays for the cases where retrieval misses something, but becomes a fallback rather than the primary path. Bound it to 0 rounds by default (off); turn it on with `review.agentic_context: true` if you want the second LLM round-trip.

### v1: Embedding-augmented retrieval

For natural-language similarity (the kind of thing tree-sitter doesn't help with: "this looks like it could be relevant to error handling around the new method"), add a second retrieval lane.

- Embed chunks at indexing time using a local model (`bge-small-en-v1.5` is a reasonable default, runs cheaply on CPU; for cloud, voyage-3-lite or text-embedding-3-small).
- Store embeddings in SQLite using the `sqlite-vec` extension. No separate vector database. The selfhost story stays one process, one disk file.
- Query at retrieval time using the diff (or each hunk) as the query.
- Merge with the symbol-graph hits, dedupe, cap.

This is genuinely useful for documentation or test-discovery, less useful for the typed-language code review case where symbol resolution wins. Build v0 first, run the eval harness against it, see what's missing before adding v1.

### Cache lifecycle

Indexing a mid-size repo (10K files) with tree-sitter and embeddings is on the order of 30 to 60 seconds the first time. After that:

- Cache key is `(owner/repo, sha)`. PR re-reviews hit the same cache entry.
- A new SHA on the same repo triggers an incremental update: re-index only changed files (use the existing compare API).
- LRU evict to a fixed disk cap, default 5GB.

A new `ReviewBot.Retrieval` project, with `IRepoIndex`, `ITreeSitterParser`, `IEmbeddingClient`, `IRetrievalProvider`. Injected into the worker the same way grounding is.

### Config

```yaml
retrieval:
  enabled: true                        # default true once shipped
  max_bytes: 102400                    # cap on retrieved context per review
  symbol_lookup_depth: callers         # callers | definitions | both
  embeddings: false                    # v0 ships with this off
  index_cache_dir: /var/cache/reviewbot/index
```

### What this replaces

- `review.full_file_max_bytes`: subsumed. Retrieval can include the modified file's full text if it's small, but without a separate config flag. The retrieval engine decides.
- `review.agentic_context`: stays but defaults off. Use it for cases where the model recognizes a need retrieval missed.

### Eval signal

This is where the harness pays off. Compare v0 retrieval vs current agentic-context approach across the fixture corpus. Expected outcomes:

- Symbol-graph retrieval matches or beats agentic context on the "needs definition of referenced type" fixtures.
- Symbol-graph retrieval is dramatically faster (one parallel batch query vs second LLM round-trip).
- Agentic context still wins on a small subset of fixtures where the bug is in a file the diff doesn't structurally reference. Use this to decide whether to keep agentic as a fallback or remove it entirely.

---

## Phase 23: Observability and review traces

The existing metrics are good for system health (queue depth, processing rate, LLM duration). They are not good for debugging *why a specific review was bad*, which is what you'll need to triage user reports as adopters come on board.

### Review trace persistence

For every review, write a single JSON file containing:

- Job metadata (delivery ID, owner, repo, PR, SHA, trigger reason)
- Config snapshot (the effective `ReviewConfig` for this review)
- Full prompt sent to the LLM (system + user)
- Raw LLM response (pre-parse)
- Parsed `ReviewResult`
- Self-critique prompt/response/result if it ran
- Agentic context request + fetched files if applicable
- Retrieval hits if applicable (Phase 22)
- Final comments posted vs filtered, with drop reasons
- Per-stage timings
- Token usage

Stored under `traces/{repo}/{prNumber}-{deliveryId}.json`, with size cap and TTL via the existing cleanup service. Off by default (`Tracing__Enabled: false`); on by default in dev appsettings.

This is gold for debugging. "User says the bot flagged something weird on PR #42, here's the trace JSON, paste it back to me." Also: critical for the eval harness, since you can replay any saved trace through the bot for regression testing.

### OpenTelemetry spans

Wrap the major worker stages in `ActivitySource` spans, named consistently:

```
reviewbot.review
├─ reviewbot.fetch_token
├─ reviewbot.fetch_config
├─ reviewbot.fetch_pr_files
├─ reviewbot.grounding
│  ├─ reviewbot.grounding.tier1_language
│  ├─ reviewbot.grounding.tier2_build
│  └─ reviewbot.grounding.tier3_tests
├─ reviewbot.retrieval (Phase 22)
├─ reviewbot.llm.review
├─ reviewbot.llm.self_critique
├─ reviewbot.llm.agentic_context
└─ reviewbot.post_review
```

OpenTelemetry exporters are already a one-liner with `OpenTelemetry.Extensions.Hosting`. Add OTLP exporter, document the Jaeger/Tempo target in the README. Self-hosters who care about traces can wire to their own collector; the rest get the metrics they already have.

### Cost surface

When the LLM provider returns token usage (Phase 19), compute an estimated dollar cost per review using a configurable per-token rate. Surface as:

- A counter `reviewbot.cost.usd_total` with labels (provider, model, phase).
- A field in the trace JSON.
- Eventually a column in the WebUI's review list.

For local Ollama models the cost is zero; the counter just stays at zero. For cloud models the company adopters care a lot about this number.

---

## Phase 24: WebUI

Defer until Phases 19 to 23 ship. The CLI plus logs plus traces are enough for development. The WebUI is for the moment a non-developer admin or a tech lead wants to look at the bot's behavior without grepping JSON.

### Scope for v1

Blazor Server, same .NET 10 stack as the API. Single project, served from the same host. No separate frontend toolchain (you're a single maintainer; the JS ecosystem is not your friend here).

Pages:

- **Reviews list.** Recent reviews across all installations, filterable by repo and status (success / skipped / failed). Click into one.
- **Review trace detail.** The trace JSON, rendered. System prompt, user prompt, raw LLM response (collapsed by default, expand to read), parsed comments, posted comments, dropped comments with reasons, per-stage timings, token usage, cost estimate.
- **Metrics dashboard.** Existing OTel metrics rendered as charts: reviews per hour, p50/p95 latency, token usage trend, cost trend.
- **Config editor.** Pull the `.github/review-bot.yml` from any installed repo via the GitHub API, render with validation, let the user edit and commit (via the same GitHub App, requires Contents:write so make it opt-in). Useful for tuning without bouncing to GitHub's web editor.
- **Eval results browser.** Latest eval harness run, fixture-by-fixture pass/fail, comment-quality scores, diff against the previous run.

What's explicitly out of scope for v1: multi-tenant auth, per-user permissions, org-level admin. This is a self-hosted single-tenant tool. One Basic Auth password protecting the whole UI is fine; document it in the README.

---

## Things you didn't mention that probably belong on the list

**Multi-pass chunked review for large PRs.** Today the patch-budget logic drops files. Better: when total patch lines exceed the budget, split into N coherent chunks (group files by directory or import-graph proximity), review each chunk independently, merge the comments. Quality move for refactor PRs that touch 100 files. Worth doing after retrieval is in place because retrieval also helps cross-chunk consistency.

**Severity calibration.** The LLM picks `info | warning | error`, and the `request_changes_on_error` flag promotes any error to a merge block. This is dangerous if the model over-uses `error`. The eval harness should report severity distribution by model, and the system prompt should include a calibration anchor ("`error` means this PR cannot be merged in its current state; reserve for security issues, data loss, or correctness regressions in critical paths"). Otherwise users will turn off `request_changes_on_error` after the first false-positive merge block.

**Pre-LLM secret scrub.** Your company will care about this. Before constructing the prompt, run a fast regex pass over the diff and any retrieved/fetched files for the common shapes: AWS access keys, GitHub PATs, Stripe keys, private key blocks, RSA / EC private headers, hardcoded passwords. Either redact or refuse to send the review with a clear log line. The agentic-context fetcher already does path-based scrubbing (`.env`, `*.pem`); content-based scrubbing closes the gap when a secret ends up in a `.cs` file by mistake.

**Inline `suggestion` blocks.** GitHub renders ```` ```suggestion ```` blocks in review comments as one-click-applyable diffs. The current prompt mentions them but the model rarely produces them. A focused prompt-engineering pass plus a few eval fixtures that grade on "did the model produce an applicable suggestion when the fix is mechanical" would push this. Big quality-of-life win for adopters.

**Severity-aware notifications.** When `request_changes_on_error` fires, send an extra signal beyond the GitHub review (Slack webhook, email, whatever). Optional, off by default, configured per-install. Useful for teams that monitor the bot's output as a CI signal.

**One-command Docker bring-up for the self-host story.** A `docker-compose.yml` in the repo root that runs ReviewBot plus Ollama plus a model puller, with sensible defaults. New user clones the repo, fills in three env vars (GitHub App ID, private key, webhook secret), runs `docker compose up`. This is what makes the Copilot-pricing-driven adopters say yes vs no. Today the README has a `docker run` snippet that assumes you've already built the image; that's a higher bar than necessary.

**A public eval scoreboard.** Once Phase 20 lands, run the eval against the major models (Claude Opus 4.7, Sonnet 4.6, GPT-5.1, GPT-4.1, Qwen 9B, Llama 3.1 70B, Llama 3.1 8B), publish the table in the README. This is excellent marketing: "ReviewBot's quality is a function of the model you point it at, and here's the data." Updates per model release.

**Conversation continuity.** When a PR author replies to a bot comment with "this is intentional, see ADR-042," the bot should not re-flag the same issue on the next push. Hard problem: requires tracking comment threads, classifying replies (acknowledged / disputed / unrelated), and selective re-review. Defer to v2. Mention in the docs as a known limitation.

**License care on eval fixtures.** If you derive fixtures from public OSS PRs, the source repo's license matters for redistribution. MIT/Apache/BSD fixtures are fine. GPL is sticky. Avoid kernel-style projects unless you only ship a pointer to the upstream SHA, not a checked-in copy.

**Test runner expansion.** Phase 18's Tier 3 grounding only supports .NET and Python. Once the eval harness is live, adding JS/TS (tsc, jest), Go (go test), and Rust (cargo test) becomes cheap because you can verify each one against fixtures specific to that language. Prioritize JS/TS first; the market is bigger.

---

## What's intentionally not on this list

- Multi-tenant cloud product (you've explicitly deferred).
- Anything that requires breaking changes to the v0.2 config schema. New features add fields with defaults; nothing removes existing flags.
- Model fine-tuning. The model-agnostic interface stays. If a fine-tuned model outperforms on the eval harness, the worker is happy to point at it; that's an exercise for an adopter, not the project.
- A formal plugin/extension API. Premature. The IReviewLlm and IRepoConfigFetcher seams already exist; that's enough surface area for forks. Promote to a real API once there's evidence anyone wants to extend the bot in a way the config doesn't cover.

---

## Risks

The eval harness is the load-bearing piece of this plan. If it isn't built carefully (judge prompt drift, fixture distribution skewed toward easy bugs, scoring rules too forgiving), the rest of the work optimizes against a bad metric. Budget at least three days for rubric-tuning after the initial implementation, comparing harness scores against your own hand-review of the same fixtures. If the harness and your eye disagree on more than 20 percent of fixtures, the rubric needs work, not the bot.

Retrieval in Phase 22 has integration surface area: tree-sitter native binaries on multiple OS targets, sqlite-vec extension loading, disk cache management. Plan for two weeks of "make it actually work on Linux containers, Mac dev machines, Windows runners" after the happy-path implementation. Selfhosters will hit these.

Anthropic SDK 5.x is still unofficial. The risk hasn't changed since the v4 plan, but the surface area grows when Phase 21 adds prompt caching. Worth investigating whether the official Anthropic .NET SDK has shipped or is close; if so, migrate. If not, pin the version and document.
