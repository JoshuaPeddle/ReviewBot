# ReviewBot Development Plan

## Current state (v2, 2026-05-24)

Phases 1–16 complete (Steps 1–48 + command-override follow-up). Stop test: `dotnet test --no-restore` → 352 passing, 0 failed, 1 skipped (Ollama E2E).

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

**Key implementation decisions:**
- EF Core migrations must be generated via `dotnet ef migrations add`; hand-crafted snapshots fail with `PendingModelChangesWarning` promoted to error.
- Octokit 14 model classes (`CompareResult`, `GitHubCommitFile`, etc.) have non-virtual properties — construct via public constructors in tests, never `Substitute.For<T>()`. Compare API is `client.Repository.Commit.Compare` (singular).
- `ReviewPoster` posts one review with a `comments` array; E2E assertions inspect the single review payload, not per-comment API calls.
- `review_requested` event (not `opened`) triggers the first-review path in the webhook handler.
- Anthropic LLM adapter has no configurable base URL; WireMock E2E scenarios must use the OpenAI-compatible provider via repo config.
- `OpenAiLlmOptions` is bound eagerly at startup; E2E overrides must replace the singleton in `ConfigureTestServices`.

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

## Phase 17: Review state from severity

**The problem.** The bot always posts as `COMMENT`, making it advisory-only regardless of finding severity. A security vulnerability and a style nit produce the same GitHub review state.

**The approach.** Map the highest-severity surviving comment to the GitHub review event: `error` → `REQUEST_CHANGES`, warning/info only → `COMMENT`, no surviving comments → optionally `APPROVE`. Both escalation behaviors are opt-in.

### Step 49

**`ReviewBot.Core/Domain/ReviewConfig.cs`:** Add to `ReviewOutputConfig`:
```csharp
bool RequestChangesOnError = false
bool ApproveIfClean = false
```

**`ReviewBot.GitHub/Pulls/ReviewPoster.cs`:** Add `PullRequestReviewEvent reviewEvent` parameter to `PostAsync`; pass through to Octokit's `PullRequestReviewCreate.Event`.

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
- Poster: `reviewEvent` passed through to Octokit.
- Config: YAML parsing for both flags.

**E2E:** assert `event` field in `POST /repos/owner/repo/pulls/1/reviews` body is `REQUEST_CHANGES` when repo config has `request_changes_on_error: true` and LLM returns an error-severity comment.

---

## Phase 18: Full-file context for small modified files

**The problem.** Diffs contain only changed lines plus a few lines of context. A one-line change in a 50-line file leaves the model unable to see the class signature or field declarations 20 lines away, producing false positives on issues already handled nearby.

**The approach.** For modified files under a configurable byte threshold, fetch the full file content via the Contents API (already wired from Phase 15) and prepend it to that file's diff in the prompt. Disabled by default.

### Step 50

**`ReviewBot.Core/Domain/ReviewConfig.cs`:** Add to `ReviewOutputConfig`:
```csharp
int FullFileMaxBytes = 0  // 0 = disabled
```

**`ReviewBot.Api/Workers/ReviewWorker.cs`:** After file filtering and before prompt construction, when `FullFileMaxBytes > 0`:
- Collect modified (non-deleted) files where `EstimatePatchBytes(f) <= FullFileMaxBytes`
- Fetch full content via `PullRequestFetcher.GetFileContentsAsync` (reuses Phase 15 logic; same 404/binary/oversized rejection applies)
- Pass resulting `IReadOnlyDictionary<string, string> fullFileContents` to `PromptBuilder`

**`ReviewBot.Core/Prompting/PromptBuilder.cs`:** `BuildUserPrompt` gains optional `IReadOnlyDictionary<string, string>? fullFileContents`. When a file's path is present, prepend a `### Full file: {path}` fenced block before its diff section.

**`RepoConfigFetcher`:** parse `review.full_file_max_bytes` as positive integer; 0 or absent → disabled.

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

### Step 51: Structured output mode

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

### Step 52: Parse-failure repair and token usage metrics

**Parse repair** in `OpenAiReviewLlm.ReviewAsync` (and `AnthropicReviewLlm` for consistency):

On `LlmResultParser.Parse` failure:
1. Log Warning with raw response truncated to 500 chars.
2. Build repair payload: system = "Your previous response was not valid JSON. Return only a JSON object matching this schema: {schema}"; user = failed raw response.
3. Call API once more; parse result.
4. On second failure: log Warning "Repair failed"; return empty result.

Repair is skipped when cancellation is already requested.

**Token usage metrics:** parse `usage` from every chat completion response:
- Emit `reviewbot.llm.tokens` histogram with labels `direction=prompt|completion` and `phase=review|self_critique|agentic_context`
- Log `cached_tokens` (from `usage.prompt_tokens_details.cached_tokens`) at Debug when non-zero — indicator of server-side KV cache hits on vLLM
- Counter `reviewbot.llm.parse_failures_total` with label `repaired=true|false`

**Tests:**
- Repair: first parse fails → repair call made → success → result returned; second parse also fails → Warning, empty result.
- Token metrics: response with `usage` → histogram recorded; missing `usage` → no exception.
- `parse_failures_total` incremented correctly.

**E2E:** stub LLM to return malformed JSON on first call, valid JSON on repair call; assert review is posted and `parse_failures_total` counter is non-zero via metrics endpoint.

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
