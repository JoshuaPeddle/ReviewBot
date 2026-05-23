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

## Architecture

- `src/ReviewBot.Api` — ASP.NET Core host, webhook endpoint, background worker
- `src/ReviewBot.Core` — domain types, prompting, LLM interfaces
- `src/ReviewBot.GitHub` — GitHub API (auth, PR fetching, review posting, repo config)
- `src/ReviewBot.Grounding` — language detection, build/test runners, workspace cloning
- `src/ReviewBot.Llm.Anthropic` — Anthropic SDK LLM provider
- `src/ReviewBot.Llm.OpenAi` — OpenAI-compatible LLM provider (also used for Ollama)
- `src/ReviewBot.Persistence` — SQLite via EF Core (idempotency, incremental review state)
