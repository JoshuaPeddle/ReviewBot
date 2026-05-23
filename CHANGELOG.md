# Changelog

All notable changes to this project will be documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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
