# ReviewBot

A GitHub App that automatically reviews pull requests using an LLM.

## Running tests

```
dotnet test                        # all unit/integration tests (fast, no external deps)
dotnet test tests/ReviewBot.E2eTests/  # LLM e2e test (see below)
```

## E2E test (LLM in the loop)

`tests/ReviewBot.E2eTests/OllamaReviewE2eTests.cs` runs the full pipeline — webhook → worker → real LLM → captured review result. It uses Ollama via the OpenAI-compatible API.

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

If you only need a smoke check that doesn't hit the LLM, `make eval-quick` scores against the committed canned results.

## Architecture

- `src/ReviewBot.Api` — ASP.NET Core host, webhook endpoint, background worker
- `src/ReviewBot.Core` — domain types, prompting, LLM interfaces
- `src/ReviewBot.GitHub` — GitHub API (auth, PR fetching, review posting, repo config)
- `src/ReviewBot.Grounding` — language detection, build/test runners, workspace cloning
- `src/ReviewBot.Llm.Anthropic` — Anthropic SDK LLM provider
- `src/ReviewBot.Llm.OpenAi` — OpenAI-compatible LLM provider (also used for Ollama)
- `src/ReviewBot.Persistence` — SQLite via EF Core (idempotency, incremental review state)
- `src/ReviewBot.Retrieval` — SQLite symbol index, C# parser, retrieval provider
