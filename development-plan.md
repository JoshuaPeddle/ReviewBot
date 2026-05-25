# ReviewBot Development Plan

## Current state (v7, 2026-05-24)

Phases 1–19 complete (Steps 1–52 + command-override follow-up). Phase 19 Step 51 includes the `UseJsonMode` removal follow-up. Stop test: `dotnet test --no-restore` → 378 passing, 0 failed, 1 skipped (Ollama E2E).

**Capabilities shipped:**
- GitHub App webhook handling, signature validation, idempotent delivery tracking (SQLite)
- LLM-backed PR review via Anthropic SDK and OpenAI-compatible endpoints (Ollama, vLLM)
- Inline comments with `severity` (info/warning/error) and `confidence` (low/medium/high); `review.min_confidence` filtering
- Self-critique second pass (`review.self_critique`): retains high-confidence comments; LLM votes on lower-confidence ones
- Agentic context fetching (`review.agentic_context`): model requests up to N repo files; worker validates paths, enforces ignore globs, rejects secrets/binaries before fetching
- Incremental reviews: tracks last-reviewed HEAD SHA per PR; diffs only changed files on re-push
- Grounding Tier 1 (language metadata), Tier 2 (local builds: .NET, Python), Tier 3 (GitHub Checks/statuses + local test runners); `build_command`/`test_command` overrides honored
- E2E infrastructure: WireMock + `WebApplicationFactory<Program>`; baseline scenarios cover review-requested, idempotency, empty diff, all-ignored, self-critique, agentic context, GitHub Checks
- Metrics: skip-reason counter, grounding duration histogram, LLM duration histogram (phase label), incremental-review type counter
- Review-state escalation from surviving comment severity: opt-in `review.request_changes_on_error` maps error-severity comments to `REQUEST_CHANGES`; opt-in `review.approve_if_clean` maps clean reviews to `APPROVE`
- Full-file context for small changed files: opt-in `review.full_file_max_bytes` fetches non-deleted changed files under the threshold and prepends full text before each diff in the review prompt
- OpenAI-compatible adapter response-format selection: `OpenAi.ResponseFormat` supports `json_object`, `json_schema`, and `text`; raw self-critique/agentic passes always use text mode
- Parse-failure repair: OpenAI-compatible and Anthropic review passes perform one schema-guided repair call on malformed review JSON and return an empty review if repair also fails
- OpenAI-compatible token usage telemetry: records `reviewbot.llm.tokens` by prompt/completion direction and review/self-critique/agentic-context phase; parse failures record `reviewbot.llm.parse_failures_total`

**Key implementation decisions:**
- EF Core migrations must be generated via `dotnet ef migrations add`; hand-crafted snapshots fail with `PendingModelChangesWarning` promoted to error.
- Octokit 14 model classes (`CompareResult`, `GitHubCommitFile`, etc.) have non-virtual properties — construct via public constructors in tests, never `Substitute.For<T>()`. Compare API is `client.Repository.Commit.Compare` (singular).
- `ReviewPoster` posts one review with a `comments` array; E2E assertions inspect the single review payload, not per-comment API calls.
- `review_requested` event (not `opened`) triggers the first-review path in the webhook handler.
- Anthropic LLM adapter has no configurable base URL; WireMock E2E scenarios must use the OpenAI-compatible provider via repo config.
- `OpenAiLlmOptions` is bound eagerly at startup; E2E overrides must replace the singleton in `ConfigureTestServices`.
- `ReviewPoster` sends raw review JSON through Octokit's connection; map `PullRequestReviewEvent` to GitHub's uppercase wire values (`COMMENT`, `REQUEST_CHANGES`, `APPROVE`) explicitly.
- Non-`COMMENT` reviews must still post when they have no accepted comments and no summary; clean approvals use the default body instead of being skipped.
- Full-file context is carried on `ReviewRequest` because LLM adapters own prompt construction through `PromptBuilder.Build(request)`.
- `review.full_file_max_bytes` is a non-negative integer: `0` is a valid disabled value, unlike other positive-only review limits.
- `OpenAi.ResponseFormat` is the only OpenAI-compatible response-format switch; the old `OpenAi.UseJsonMode` flag was removed because ReviewBot is not deployed outside this repo yet.
- LLM telemetry lives in `ReviewBot.Core` on the shared `ReviewBot` meter because LLM adapter projects cannot depend on `ReviewBot.Api`; API metrics reuse the same meter name.
- `CompleteRawAsync` carries an optional phase label so OpenAI-compatible token metrics distinguish `review`, `self_critique`, and `agentic_context` calls.

---

## Open risks

- Dockerfile has not been exercised with an actual `docker build`.
- `Anthropic.SDK` 5.10.0 is unofficial; revisit when an official .NET SDK ships.
- `DeliveryStoreCleanupServiceTests` has timing-sensitive flakes under parallel full-solution runs; pass on immediate targeted rerun.
- Octokit 14 non-virtual model properties: always construct in tests via public constructors.
- Anthropic E2E base URL not injectable; all WireMock scenarios use OpenAI-compatible provider.
- Local build/test execution requires SDKs on the worker; keep SDK-dependent E2E tests behind `[Trait("Category", "RequiresSdk")]`.
- Branch-protection "required" check context not distinguished; `CheckRunFetcher` treats any completed failing check/status as failed.

---

## Phase 17: Review state from severity — complete

**The problem.** The bot always posts as `COMMENT`, making it advisory-only regardless of finding severity. A security vulnerability and a style nit produce the same GitHub review state.

**The approach.** Map the highest-severity surviving comment to the GitHub review event: `error` → `REQUEST_CHANGES`, warning/info only → `COMMENT`, no surviving comments → optionally `APPROVE`. Both escalation behaviors are opt-in.

### Step 49 — complete (2026-05-24)

Implemented end-to-end:
- Added `ReviewOutputConfig.RequestChangesOnError` and `ApproveIfClean`, parsed from `review.request_changes_on_error` and `review.approve_if_clean`.
- Worker now chooses `REQUEST_CHANGES` when the final filtered comments include `Severity.Error` and escalation is enabled, `APPROVE` when no comments survive and clean approval is enabled, otherwise `COMMENT`.
- `ReviewPoster.PostAsync` accepts a `PullRequestReviewEvent` and writes the corresponding GitHub review `event` field.
- Documentation covers both repo config flags and calls out that `request_changes_on_error` can block merges.
- Unit tests cover worker event selection, poster event serialization/empty approval posting, and YAML parsing.
- E2E test asserts the review POST body contains `REQUEST_CHANGES` for an error-severity LLM comment with `request_changes_on_error: true`.

Corrected assumptions discovered during implementation:
- Because the poster sends raw JSON rather than Octokit's typed review model, the event must be serialized as GitHub's uppercase API strings, not `PullRequestReviewEvent.ToString()`.
- `approve_if_clean` can intentionally produce a review with no inline comments; the poster's previous "empty summary and no valid comments" skip is only correct for advisory `COMMENT` reviews.

Stop test: `dotnet test --no-restore` passes (360 passing, 0 failed, 1 skipped Ollama E2E).

**`ReviewBot.Core/Domain/ReviewConfig.cs`:** Add to `ReviewOutputConfig`:
```csharp
bool RequestChangesOnError = false
bool ApproveIfClean = false
```

**`ReviewBot.GitHub/Pulls/ReviewPoster.cs`:** Add `PullRequestReviewEvent reviewEvent` parameter to `PostAsync`; serialize it into the raw review payload's `event` field.

**`ReviewBot.Api/Workers/ReviewWorker.cs`:** After final comment filtering:
```csharp
var reviewEvent = PullRequestReviewEvent.Comment;
if (config.Review.RequestChangesOnError && finalComments.Any(c => c.Severity == Severity.Error))
    reviewEvent = PullRequestReviewEvent.RequestChanges;
else if (config.Review.ApproveIfClean && finalComments.Length == 0)
    reviewEvent = PullRequestReviewEvent.Approve;
```

**`RepoConfigFetcher`:** parse `review.request_changes_on_error` and `review.approve_if_clean` booleans.

**`docs/configuration.md`:** Document both flags; note `request_changes_on_error` actively blocks merges.

**Tests:**
- Worker: error comment + flag → `RequestChanges`; warning only → `Comment`; empty + `ApproveIfClean` → `Approve`; empty without flag → `Comment`.
- Poster: `reviewEvent` serialized to GitHub's review payload.
- Config: YAML parsing for both flags.

**E2E:** assert `event` field in `POST /repos/owner/repo/pulls/1/reviews` body is `REQUEST_CHANGES` when repo config has `request_changes_on_error: true` and LLM returns an error-severity comment.

---

## Phase 18: Full-file context for small modified files — complete

**The problem.** Diffs contain only changed lines plus a few lines of context. A one-line change in a 50-line file leaves the model unable to see the class signature or field declarations 20 lines away, producing false positives on issues already handled nearby.

**The approach.** For modified files under a configurable byte threshold, fetch the full file content via the Contents API (already wired from Phase 15) and prepend it to that file's diff in the prompt. Disabled by default.

### Step 50 — complete (2026-05-24)

Implemented end-to-end:
- Added `ReviewOutputConfig.FullFileMaxBytes` with default `0` (disabled) and parsed `review.full_file_max_bytes` from repo YAML.
- Worker now selects non-deleted files whose UTF-8 patch byte estimate is at or below the threshold, fetches full content via `PullRequestFetcher.GetFileContentsAsync`, and continues diff-only if fetching fails or every candidate is rejected as missing, oversized, binary, or invalid UTF-8.
- `ReviewRequest` now carries optional full-file content so existing LLM adapters can continue calling `PromptBuilder.Build(request)`.
- `PromptBuilder` prepends `### Full file: {path}` fenced content before the matching diff section and sanitizes null bytes/newlines using the same fetched-content sanitizer as agentic context.
- Documentation covers `review.full_file_max_bytes` and warns that it increases prompt size.
- Unit tests cover prompt placement, worker selection/disabled behavior, and config parsing including valid `0`.
- E2E test stubs the Contents API for a small `.cs` file and asserts the OpenAI-compatible request body contains the full-file section before the diff hunk.

Corrected assumptions discovered during implementation:
- The worker does not build the primary prompt directly; the full-file map must travel on `ReviewRequest` for the LLM adapters to include it.
- The new numeric option cannot reuse the existing positive-only config merge helper because explicit `0` is a valid disabled state.
- No new risk was opened. Prompt-size growth is mitigated by default-disabled behavior and the per-repo byte threshold.

Stop test: `dotnet test --no-restore` passes (365 passing, 0 failed, 1 skipped Ollama E2E).

**`ReviewBot.Core/Domain/ReviewConfig.cs`:** Add to `ReviewOutputConfig`:
```csharp
int FullFileMaxBytes = 0  // 0 = disabled
```

**`ReviewBot.Api/Workers/ReviewWorker.cs`:** After file filtering and before prompt construction, when `FullFileMaxBytes > 0`:
- Collect modified (non-deleted) files where `EstimatePatchBytes(f) <= FullFileMaxBytes`
- Fetch full content via `PullRequestFetcher.GetFileContentsAsync` (reuses Phase 15 logic; same 404/binary/oversized rejection applies)
- Carry resulting `IReadOnlyDictionary<string, string> fullFileContents` on `ReviewRequest` so LLM adapters pass it to `PromptBuilder`

**`ReviewBot.Core/Prompting/PromptBuilder.cs`:** `BuildUserPrompt` reads optional `ReviewRequest.FullFileContents`. When a file's path is present, prepend a `### Full file: {path}` fenced block before its diff section.

**`RepoConfigFetcher`:** parse `review.full_file_max_bytes` as a non-negative integer; 0 or absent → disabled.

**`docs/configuration.md`:** Document `review.full_file_max_bytes`; note it increases prompt size and should be tuned against the model's context window.

**Tests:**
- Worker: `FullFileMaxBytes = 10000`, small file → `GetFileContentsAsync` called; large file → skipped; disabled → never called.
- `PromptBuilder`: file in dict → full-file section before diff; absent → diff only.
- Config: YAML parsing.

**E2E:** stub Contents API for a small `.cs` file; assert LLM request body contains the full-file section before the diff hunk.

---

## Phase 19: OpenAI-compatible robustness

**The problem.** Self-hosted models (Ollama, vLLM, llama.cpp) vary in JSON mode support. Parse failures return an empty result with no recovery. Token usage is invisible, so there is no signal for context-window pressure.

**The approach.** Add a configurable response format mode, a single repair retry on parse failure, and token usage metrics from the API response. Focus is the OpenAI-compatible adapter.

### Step 51 — complete (2026-05-24)

Implemented end-to-end:
- Added `OpenAiLlmOptions.ResponseFormat` with accepted values `json_object`, `json_schema`, and `text`; default behavior remains `json_object`.
- Removed the previous `UseJsonMode` flag; deployments configure response behavior only through `ResponseFormat`.
- OpenAI review calls now send the configured response format, including a structured-output JSON schema for `json_schema`.
- The JSON schema matches the review shape (`summary`, `comments`, `path`, `line`, `severity`, `confidence`, `body`) and includes optional `context_requests` when agentic context is enabled.
- `CompleteRawAsync` now always uses text mode so self-critique and agentic-context passes are not constrained by the primary review schema.
- Documentation covers `OpenAi.ResponseFormat`, its environment variable, and the related `.github/review-bot.yml` notes for OpenAI-compatible providers.
- Base appsettings now declares `ResponseFormat: json_object`; development appsettings uses `ResponseFormat: text` for the local OpenAI-compatible/Ollama default.
- Unit tests cover request construction for `json_object`, `json_schema`, and `text`, raw completions forcing text mode, schema content, SDK response-format mapping, and validation.

Corrected assumptions discovered during implementation:
- Compatibility with the old `UseJsonMode` flag is not required while ReviewBot is only deployed in this repo, so the old flag was removed instead of bridged.
- `CompleteRawAsync` was still using the primary review JSON mode, which is wrong for self-critique and agentic-context prompts with non-review schemas.

Risk register update:
- No new risk opened. JSON-mode incompatibility is now mitigated by `ResponseFormat: text` and `ResponseFormat: json_schema`.
- Follow-up risk from Step 51 was closed in Step 52 by parse repair and token usage metrics.

Stop test: `dotnet test --no-restore` passes (376 passing, 0 failed, 1 skipped Ollama E2E).

Follow-up (2026-05-24):
- Removed the old `OpenAi.UseJsonMode` option entirely; `OpenAi.ResponseFormat` is now the only host-level response-format setting.
- Updated appsettings, E2E harness overrides, docs, and `.github/review-bot*.yml` comments to use or point to `ResponseFormat`.
- Removed the legacy fallback unit test.

Stop test after follow-up: `dotnet test --no-restore` passes (375 passing, 0 failed, 1 skipped Ollama E2E).

**`ReviewBot.Llm.OpenAi/OpenAiLlmOptions.cs`:** Add:
```csharp
public string ResponseFormat { get; set; } = "json_object";
// accepted: json_object | json_schema | text
```

**`ReviewBot.Llm.OpenAi/OpenAiReviewLlm.cs`:** In the request builder:
- `json_object`: current behavior (`response_format: {"type": "json_object"}`).
- `json_schema`: pass the full review JSON Schema via `response_format: {"type": "json_schema", "json_schema": {"name": "review_response", "strict": false, "schema": {...}}}`. `strict: false` for compatibility; not all self-hosted endpoints enforce it. Schema matches the prompt-embedded schema: `summary` (string), `comments` (array with `path`, `line`, `severity`, `confidence`, `body`), plus optional `context_requests` when agentic context is enabled.
- `text`: omit `response_format` entirely; rely on the parser's fenced-JSON extraction.

`CompleteRawAsync` (self-critique, agentic context passes) always uses `text` mode — those passes have non-standard schemas not covered by the review schema object.

Config key: `OpenAi__ResponseFormat` env var or `OpenAi.ResponseFormat` in appsettings.

**Tests:** request serialization under each mode; existing tests pass with `json_object` default.

### Step 52 — complete (2026-05-24)

Implemented end-to-end:
- Replaced the old malformed-response retry in `OpenAiReviewLlm` and `AnthropicReviewLlm` with a schema-guided repair call: system prompt contains the review JSON schema, user prompt is the failed raw response.
- Repair is skipped when cancellation has already been requested after the first parse failure.
- First parse failures log the parse error and raw response truncated to 500 characters; second repair failures log and return an empty `ReviewResult` instead of throwing.
- Added `ReviewBotLlmMetrics` on the shared `ReviewBot` meter with `reviewbot.llm.parse_failures_total` and `reviewbot.llm.tokens`.
- Changed the internal OpenAI chat client result to carry optional token usage; the SDK adapter records prompt/completion token histograms and logs cached prompt tokens at Debug when non-zero.
- Added phase labels for raw completions (`self_critique`, `agentic_context`) by extending `CompleteRawAsync` with an optional phase parameter and passing explicit phases from the worker.
- Reused a shared review JSON schema builder for OpenAI structured-output requests and repair prompts.
- Unit tests cover OpenAI and Anthropic repair success/failure behavior, OpenAI parse-failure metrics, token metrics, and worker phase labels.
- E2E test stubs malformed OpenAI-compatible output followed by repaired valid JSON, then asserts a review is posted and the parse-failure metric is emitted.

Corrected assumptions discovered during implementation:
- The existing adapters already retried malformed JSON, but the retry reused the original prompt plus a terse instruction and threw on second failure; Step 52 needed a separate repair prompt and non-throwing fallback.
- There is no HTTP metrics endpoint in the current app. Existing tests verify `System.Diagnostics.Metrics` with `MeterListener`, so the E2E repair test follows that pattern instead of asserting via an endpoint.
- Token usage is only available from the OpenAI-compatible chat completion response in the current adapter shape; Anthropic repair was implemented for consistency, but Anthropic token telemetry was not added.
- `parse_failures_total` records one malformed-review incident by final repair outcome (`repaired=true|false`), not one count for each invalid response body.

Risk register update:
- Closed: malformed primary OpenAI-compatible review responses no longer fail the job after the old retry path; one repair attempt is made and repair failure degrades to an empty review.
- Closed: OpenAI-compatible token usage is no longer invisible when the provider returns `usage`.
- No new risk opened. Metrics endpoint exposure remains intentionally absent; tests continue to observe the shared meter directly.

Stop test: `dotnet test --no-restore` passes (378 passing, 0 failed, 1 skipped Ollama E2E).

---

## What is intentionally excluded

- No web UI for admin or stats.
- No multi-LLM ensemble or voting across providers.
- No support for reply threads on existing review comments.
- No GitHub Enterprise Server (only github.com).
- No fine-grained per-file model selection.
- No Tier 3 grounding for languages other than .NET and Python.
- Agentic context fetching is bounded to one round — no recursive expansion.
- No local build/test execution by default; remains per-repo opt-in.
