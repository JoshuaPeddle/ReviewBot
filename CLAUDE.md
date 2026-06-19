# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

# ReviewBot

A self-hosted GitHub App that automatically reviews pull requests using an LLM (Anthropic, OpenAI-compatible, or local via Ollama/LM Studio/vLLM).

## Build & conventions

```
dotnet build                       # solution-wide
dotnet format                      # apply .editorconfig style
```

- Targets **.NET 10**, `Nullable` and `ImplicitUsings` enabled, **`TreatWarningsAsErrors=true`** — a new warning fails the build. Common settings live in `Directory.Build.props`.
- **Central package management**: all package versions are pinned in `Directory.Packages.props`. Add a `<PackageReference>` without a version in the `.csproj`, and the version in `Directory.Packages.props`.
- File-scoped namespaces and `this.`-qualified fields are the house style (see `.editorconfig`); match the surrounding code.

## Running tests

```
dotnet test                                            # all unit/integration/mocked-e2e tests (fast, no external deps)
dotnet test --filter "FullyQualifiedName~WebhookEndpoint"   # a single class/test by substring
dotnet test tests/ReviewBot.E2eTests/                  # real-LLM e2e (see below)
```

There are **two** end-to-end test projects — don't confuse them:

- `tests/ReviewBot.E2E.Tests` — full webhook→worker→post pipeline with **WireMock-mocked** GitHub and LLM. Runs as part of the normal `dotnet test`; no external services.
- `tests/ReviewBot.E2eTests` — drives the pipeline through a **real LLM** via Ollama (OpenAI-compatible API). Skips automatically unless configured.

## E2E test (real LLM in the loop)

`tests/ReviewBot.E2eTests/OllamaReviewE2eTests.cs` runs the full pipeline — webhook → worker → real LLM → captured review result.

**Required env vars:**

```
REVIEWBOT_E2E_OLLAMA_MODEL=granite4.1:8b-q4_K_M
REVIEWBOT_E2E_OLLAMA_URL=http://192.168.2.169:11434/v1   # defaults to localhost:11434/v1
```

The test skips automatically if `REVIEWBOT_E2E_OLLAMA_MODEL` is not set or Ollama is unreachable.

**Important:** `OpenAiLlmOptions` is bound eagerly in `AddOpenAiReviewLlm` during `Program.cs` startup, before `WebApplicationFactory`'s `ConfigureAppConfiguration` runs. The test therefore replaces the `OpenAiLlmOptions` singleton directly in `ConfigureTestServices` — do not try to override it via config keys.

## Running live evals against the reference local model

The eval gate for v0.3 is measured against `qwen/qwen3.6-27b` running in LM Studio on the LAN box at `http://192.168.2.167:1234/v1` (or whatever endpoint `.env.eval` configures). Always probe before kicking off a live run — they take ~20+ minutes each.

```
make eval-probe            # fail fast if the LLM endpoint is unreachable
make eval-live-baseline    # retrieval OFF; writes runs/eval-{ts}-baseline.json
make eval-live-retrieval   # retrieval ON;  writes runs/eval-{ts}-retrieval.json
make eval-live-compare     # both + comparison; writes runs/eval-{ts}-comparison.json
```

Outputs land in `runs/eval-{UTC-timestamp}-{label}.json` (gitignored). `EVAL_RUN_LABEL=foo make eval-live-compare` overrides the timestamp if you want a named output.

Config lives in `.env.eval` (gitignored, see `.env.eval.example`). It sets `REVIEWBOT_EVAL_BASE_URL`, `REVIEWBOT_EVAL_MODEL_NAME`, `REVIEWBOT_EVAL_OPENAI_API_KEY`, and `REVIEWBOT_EVAL_CONTEXT_TOKENS`. The Makefile sources it via `-include` and re-exports the variables, so the eval CLI picks up the API key from the env.

If you only need a smoke check that doesn't hit the LLM, `make eval-quick` scores against the committed canned results. The eval CLI itself lives in `tests/ReviewBot.Evals` (verbs: `run-live`, `score`, `compare`); fixtures are in `tests/ReviewBot.Evals/Fixtures`.

> Fixture authoring gotcha: a fixture's name and description leak into the model prompt — keep them neutral so they don't tip off the model. See the eval-fixture-design notes if available.

## Configuration & DI

- All host config is read from environment variables with the **`REVIEWBOT__`** prefix (e.g. `REVIEWBOT__GitHubApp__AppId`, `REVIEWBOT__Anthropic__ApiKey`, `REVIEWBOT__OpenAi__BaseUrl`). Wired in `src/ReviewBot.Api/Program.cs`.
- **Both** LLM providers (`AddAnthropicReviewLlm` and `AddOpenAiReviewLlm`) are always registered at startup; the actual provider for a given PR is chosen per-review from the **per-repo `.github/review-bot.yml`** in the target repository (`IReviewLlmFactory.Create(config.Model)`). Almost every behavior — provider/model, grounding tiers, retrieval, chunking, ignore globs, token budgets — is driven by that YAML (`ReviewConfig`), not by host config.

## Architecture

- `src/ReviewBot.Api` — ASP.NET Core host, webhook endpoint, background worker, tracing, cost
- `src/ReviewBot.Core` — domain types, prompting, LLM interfaces, prompt budgeting & chunk planning, job queue
- `src/ReviewBot.GitHub` — GitHub API (auth, PR fetching, review posting, repo config)
- `src/ReviewBot.Grounding` — language detection, build/test runners, workspace cloning
- `src/ReviewBot.Llm.Anthropic` — Anthropic SDK LLM provider
- `src/ReviewBot.Llm.OpenAi` — OpenAI-compatible LLM provider (also used for Ollama)
- `src/ReviewBot.Persistence` — SQLite via EF Core (idempotency, incremental review state)
- `src/ReviewBot.Retrieval` — SQLite symbol index, C# parser, retrieval provider

### The review pipeline

The spine of the whole system is **`ReviewWorker.ProcessAsync`** in `src/ReviewBot.Api/Workers/ReviewWorker.cs`. The webhook endpoint only validates the HMAC signature, dedups by delivery id, filters events, and enqueues a `ReviewJob` onto a `Channel`-backed queue; `ReviewWorker` (a `BackgroundService` with a bounded concurrency semaphore) does all the real work, end to end:

1. **Resolve token & metadata** — installation token, then PR metadata + per-repo config (concurrently when the head SHA is already known; comment-triggered jobs have no SHA and resolve it first).
2. **Incremental / delta reviews** — `IPrReviewStateStore` records the last-reviewed SHA per PR. On a re-review it compares against the new head and, when possible, restricts the file set (and retrieval indexing) to changed paths only. No changes → skip.
3. **Grounding** (`IGroundingProvider`) — optional, config-gated: language detect → build → tests.
4. **Prompt budget** (`PromptBudget` / `IReviewPromptTokenEstimator` / `IModelContextRegistry`) — the central context-assembly mechanism. The model's context window is divided into system/grounding/response-reserve, and every subsequent stage (retrieval snippets, full-file context, diff) *consumes* from the remaining budget; sections that don't fit are dropped and logged. Read this before changing what goes into a prompt.
5. **Retrieval** (`IRetrievalProvider`) — config-gated. Ensures the repo is indexed at the head SHA (clones into a workspace, full or incremental index keyed by `RepoIndexKey(owner, repo, sha)`), then injects symbol definitions + top callers referenced by the diff.
6. **Chunk planning** (`ReviewChunkPlanner`) — if the diff exceeds the remaining budget, split into directory-aware chunks reviewed in parallel (if the provider `SupportsParallelRequests`) or sequentially, then `ReviewResultMerger` dedups and rolls up severity.
7. **Self-critique & agentic context** — for the single-chunk path, a *speculative* self-critique runs in parallel with agentic context fetching; whichever path wins, the loser is cancelled. Self-critique can drop low-confidence comments.
8. **Post & record** — filter/cap comments per output config, choose the review event, post via `IReviewPoster`, write a per-review JSON trace (`IReviewTraceWriter`) + OTel spans + estimated cost, and persist the new head SHA for the next incremental review.

Failures in any optional stage (grounding, retrieval, full-file fetch) are caught and logged; the review continues with whatever context it has rather than failing the job.
