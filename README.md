# ReviewBot

A self-hosted GitHub App that reviews pull requests with any Anthropic, OpenAI-compatible, or local LLM. MIT-licensed; designed to be brought up on your own hardware with a strong local model and pointed at real production PRs.

The reference local profile is **`qwen/qwen3.6-27b` at 72K context** — runs on a 24GB NVIDIA card (3090/4090/A5000) or a 32GB+ M-series Mac with unified memory. Smaller 7B-class models (Qwen 2.5 7B, Llama 3.1 8B) work on 8–16GB GPUs with reduced quality.

Reviews fire automatically when a PR is opened, reopened, or updated, or on demand when someone comments `/review` on the PR.

## What's in the box

- **Webhook ingestion** with HMAC verification, idempotency dedup, and a background worker queue.
- **Two LLM adapters**: Anthropic (official SDK) and OpenAI-compatible (works with OpenAI, Ollama, vLLM, LM Studio, any compatible endpoint).
- **Token-aware prompt budgeting**: model context registry baked in for Claude / GPT / Qwen / Llama / Granite; Anthropic `count_tokens` for high-fidelity estimates; heuristic for everyone else.
- **Multi-pass chunked review**: large PRs are split into directory-aware chunks that fit the model's context, reviewed in parallel (cloud) or sequentially (local single-GPU), and merged with deduplication and severity rollup.
- **Repository retrieval**: SQLite-backed symbol index plus a lexical C# parser. On each review, symbols referenced by the diff are looked up; definitions and top-3 callers are injected as context. SHA-scoped, incrementally indexed on delta reviews, evicted nightly.
- **Multi-tier grounding**: language detection → build runner → test runner. Optional, gated by config; works inside containers.
- **Speculative self-critique** that runs in parallel with agentic context fetching.
- **OpenTelemetry traces** via OTLP (Jaeger, Tempo, anything OTLP-compatible) for every stage of the review pipeline: grounding tiers, retrieval extraction/lookup/indexing, chunked review, LLM, self-critique, posting.
- **Per-review JSON traces** with prompts (opt-in), raw LLM responses, dropped-comment reasons, token usage, per-stage timings, prompt budget breakdown, and estimated cost.
- **Estimated dollar cost per review** via a configurable per-million-token rate table; surfaced as a Prometheus counter and trace field.
- **Eval harness** with rule-based scoring, multi-fixture aggregation, regression comparison, and a live-runner manifest that proves which retrieval snippets were injected.

## How it works

```
GitHub ──webhook──► POST /webhook ──► Channel Queue ──► ReviewWorker
                        │                                     │
                        ▼                                     ├── Repo config (.github/review-bot.yml)
              HMAC signature check                            ├── PR metadata + files
              Idempotency dedup                               ├── Grounding (lang / build / tests)
              Event filtering                                 ├── Retrieval (symbol index + lookup)
                                                              ├── Prompt budget + chunk planning
                                                              ├── LLM (Anthropic / OpenAI-compatible)
                                                              ├── Self-critique (merged)
                                                              ├── Trace JSON + OTel spans + cost
                                                              └── Posted GitHub review
```

## Quick start — local model with Docker Compose

The fastest path: ReviewBot + Ollama + a local model, one command.

```bash
cp .env.example .env
# edit .env with your GitHub App credentials and webhook secret
docker compose up
```

The compose stack runs:

- `ollama` — pulls the model named by `REVIEWBOT_MODEL_NAME` on first start. Default: the reference profile `qwen/qwen3.6-27b` (~17 GB pull, needs a 24GB GPU or 32GB+ M-series Mac).
- `reviewbot` — the bot, listening on `:8080`.
- `jaeger` — OTel trace viewer at [http://localhost:16686](http://localhost:16686). Comment out if you have your own collector.

Confirm it's up: `curl http://localhost:8080/healthz`.

If your hardware can't host the reference model, set `REVIEWBOT_MODEL_NAME=qwen2.5:7b-instruct-q4_K_M` (or `llama3.1:8b-instruct-q4_K_M`) in `.env` before bringing the stack up. Expect lower review quality.

If you already run a separate OpenAI-compatible server (LM Studio, vLLM, a remote GPU box), comment out the `ollama` and `model-puller` services in `docker-compose.yml` and set `REVIEWBOT__OpenAi__BaseUrl` to your endpoint.

Forward GitHub webhooks to your machine with [smee.io](https://smee.io) or [ngrok](https://ngrok.com) and point your GitHub App's webhook URL at it.

See [docs/github-app-setup.md](docs/github-app-setup.md) for the GitHub App side.

## Quick start — without Docker

```bash
git clone https://github.com/your-org/ReviewBot
cd ReviewBot

export REVIEWBOT__GitHubApp__AppId=12345
export REVIEWBOT__GitHubApp__PrivateKeyPem="$(cat path/to/private-key.pem)"
export REVIEWBOT__Webhook__Secret="your-webhook-secret"
export REVIEWBOT__Anthropic__ApiKey="sk-ant-..."        # or OpenAi options for local

dotnet run --project src/ReviewBot.Api
```

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). Service listens on `http://localhost:5000`.

## Triggers

| Trigger | When |
|---|---|
| **PR opened / reopened** | Fires automatically |
| **Push to PR branch** | Fires when `review.trigger.on_push: true` in the repo config (off by default) |
| **`/review` comment** | Fires when any user comments exactly `/review` on the PR |

The bot posts a review summary that ends with a reminder about `/review` so contributors can re-request without leaving the page.

## Per-repo configuration

Put `.github/review-bot.yml` in the target repository. Every field is optional.

```yaml
enabled: true

model:
  provider: openai             # anthropic | openai (openai works for Ollama too)
  name: qwen/qwen3.6-27b       # reference profile; swap for any local or hosted model

review:
  inline_comments: true
  summary: true
  max_files: 50
  max_patch_lines: 1500
  response_reserve_tokens: 4096       # tokens reserved for the model's reply
  chunked_review: true                # split large diffs across model context
  max_chunks: 10
  chunk_headroom: 0.85                # fraction of context used per chunk
  trigger:
    on_review_request: true
    on_push: false

retrieval:
  enabled: true                       # SQLite symbol index for cross-file context
  max_bytes: 16000                    # cap on retrieved context per review
  index_cache_dir: /var/lib/reviewbot/retrieval

ignore:
  - "**/*.generated.cs"
  - "migrations/**"

focus:
  - correctness
  - security
  - concurrency
  - error_handling

instructions: |
  This is a Go service. Flag unsafe pointer casts and context misuse.
```

Full reference: [docs/configuration.md](docs/configuration.md).

## Swapping LLM providers

### Local (Ollama / vLLM / LM Studio)

```bash
export REVIEWBOT__OpenAi__BaseUrl="http://localhost:11434/v1"
export REVIEWBOT__OpenAi__ApiKey="ollama"           # required by SDK; value ignored
export REVIEWBOT__OpenAi__ResponseFormat="text"     # most local servers don't support json_object
```

```yaml
model:
  provider: openai
  name: qwen/qwen3.6-27b        # or qwen2.5:7b-instruct-q4_K_M on smaller hardware
```

### Anthropic

```bash
export REVIEWBOT__Anthropic__ApiKey="sk-ant-..."
export REVIEWBOT__Anthropic__ModelName="claude-opus-4-7"
```

```yaml
model:
  provider: anthropic
  name: claude-opus-4-7
```

### OpenAI

```bash
export REVIEWBOT__OpenAi__ApiKey="sk-..."
export REVIEWBOT__OpenAi__ModelName="gpt-5.1"
```

## Observability

### Traces (JSON, per review)

Enable per-review JSON traces:

```bash
export REVIEWBOT__Tracing__Enabled=true
export REVIEWBOT__Tracing__IncludePrompts=false   # true persists prompts + responses; disk-heavy and IP-sensitive
export REVIEWBOT__Tracing__MaxDiskMb=500
export REVIEWBOT__Tracing__RetentionDays=14
```

Files land in `traces/{owner}/{repo}/{prNumber}-{deliveryId}.json` and include the full prompt budget, per-chunk timings, retrieval snippets, candidate vs. posted comments with drop reasons, token usage, and estimated cost.

### OpenTelemetry spans

Set `OTEL_EXPORTER_OTLP_ENDPOINT` (defaults to `http://localhost:4317`). The bot emits spans for the full pipeline: `reviewbot.review`, `reviewbot.grounding.{tier1_language,tier2_build,tier3_tests}`, `reviewbot.retrieval.{extract_symbols,lookup,index_sha}`, `reviewbot.chunk_review`, `reviewbot.llm.review`, `reviewbot.llm.self_critique`, `reviewbot.post_review`.

The included `docker-compose.yml` wires up Jaeger if you uncomment the `jaeger` service; open [http://localhost:16686](http://localhost:16686) to browse traces.

### Estimated cost

Configure per-million-token rates:

```json
{
  "CostRates": {
    "Rates": {
      "claude-opus-4-7":   { "InputPer1M": 15.00, "OutputPer1M": 75.00 },
      "claude-sonnet-4-6": { "InputPer1M":  3.00, "OutputPer1M": 15.00 }
    }
  }
}
```

Local models with no rate produce no cost; the counter stays at zero.

The metric `reviewbot.cost.usd_total` is exposed alongside the existing `reviewbot.jobs.processed`, `reviewbot.llm.duration_ms`, and `reviewbot.review.comments_posted` counters.

## Eval harness

`tests/ReviewBot.Evals/` is a runnable scoring harness. Fixtures live in `tests/ReviewBot.Evals/Fixtures/` and pair a unified `diff.patch` with `expected.yaml` (`must_flag`, `must_not_flag`, `max_total_comments`, `expected_review_state`).

```bash
make eval-quick                # canned 8-fixture smoke
dotnet run --project tests/ReviewBot.Evals -- run-live \
  --base-url http://localhost:11434/v1 \
  --model qwen/qwen3.6-27b \
  --retrieval-enabled
```

The reference model for measuring retrieval quality is `qwen/qwen3.6-27b`. The historical numbers in [CHANGELOG.md](CHANGELOG.md) were taken on the smaller `qwen3.5-9b` — those remain as a record but should not be used as the v0.3 ship-gate baseline.

Live runs emit a manifest (`runs/...-manifest.json`) recording which retrieval snippets were injected per fixture so you can audit retrieval signal independent of LLM variance.

`dotnet run -- compare baseline.json candidate.json` reports regressed / improved / unchanged fixtures with per-fixture F1 deltas.

## GitHub App setup

[docs/github-app-setup.md](docs/github-app-setup.md) — required permissions (Contents:read, Issues:read, Pull requests:read/write) and event subscriptions.

## Troubleshooting

**401 on every webhook** — `Webhook__Secret` does not match the secret in GitHub App settings.

**`/review` comment not triggering** — App needs Issues:read permission and Issue comment event subscribed.

**422 when posting a review** — GitHub rejects comments on lines outside the diff. ReviewBot filters automatically; if you still see 422s, check the `ReviewPostException` log entry for accepted/dropped counts.

**"Unknown provider" warning** — `model.provider` must be `anthropic` or `openai`; anything else falls back to defaults.

**High memory on large repos** — keep `Worker__Concurrency=1` and reduce `review.max_files` or `review.max_patch_lines`.

**Local model context overflow** — set `ModelContext__Limits__<your-model>` to its real window, or rely on the baked-in registry. The worker now chunks rather than truncating when the diff exceeds the model's content budget.

## Running tests

```bash
dotnet test                          # all unit/integration tests (fast, no external deps)
dotnet test tests/ReviewBot.E2eTests # LLM in-the-loop test, requires REVIEWBOT_E2E_OLLAMA_MODEL
```

## Roadmap and history

- [development-plan.md](development-plan.md) — current roadmap and ship gate.
- [CHANGELOG.md](CHANGELOG.md) — shipped slices with corrected assumptions per phase.

## License

MIT — see [LICENSE](LICENSE).
