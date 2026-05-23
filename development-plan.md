# ReviewBot Development Plan

## Current state (v2, 2026-05-23)

Phases 1–9 complete (Steps 1–32). The bot handles PR webhooks for GitHub Apps, reviews diffs with Anthropic or any OpenAI-compatible endpoint, posts inline comments, stores idempotency in SQLite, and is configurable per-repo via `.github/review-bot.yml`. Build green, 220 tests passing. Docker image published on tags.

Grounding is fully wired end-to-end. Tier 1 (language metadata from config files via GitHub Contents API) is live for .NET and Python. Tier 2 (workspace clone + build runner) is live and opt-in via `grounding.build: true`; workspace security guidance documented in `docs/configuration.md`. `GroundingContext`, `BuildResult`, and `TestResult` types are in `ReviewBot.Core.Domain`. `CompositeGroundingProvider` handles detection, Tier 1 extraction, and Tier 2 build in a single sequential pass. `ReviewWorker` injects the grounding context into `ReviewRequest` before the LLM call.

---

## Phase 10: Parallel grounding

**The problem.** `CompositeGroundingProvider.GetContextAsync` is fully sequential: list root files → detect language → Tier 1 extraction → clone workspace → run build. When `grounding.build: true`, the git clone cannot start until Tier 1 extraction completes, even though the clone URL (`https://github.com/{owner}/{repo}.git`) is known the moment the detector matches. On a typical PR this serializes two independent network/IO operations that could overlap.

**The approach.** After the matching detector is identified, start the workspace clone as a concurrent `Task` while Tier 1 `ExtractMetadataAsync` runs. Await both with `Task.WhenAll`. The clone result is only consumed after both complete. If the clone fails while metadata extraction is in progress, log and proceed without build result — same failure mode as today. If metadata extraction fails, cancel and dispose the pre-started workspace.

### Step 33: Concurrent clone and Tier 1 extraction

**`CompositeGroundingProvider.GetContextAsync`** refactored. The change is entirely internal to the private method; the public interface, constructor signatures, and DI registration are unchanged. All 220 existing tests continue to pass with no modifications.

Revised flow inside `GetContextAsync`:

```csharp
var reader = readerFactory(request);
var rootFiles = await reader.ListRootFilesAsync(request.HeadSha, ct);
var detector = detectors.FirstOrDefault(d => d.CanDetect(rootFiles));
if (detector is null) return Empty;

// Start clone immediately; it runs concurrently with metadata extraction.
Task<IWorkspace?>? cloneTask = null;
if (request.Config.Build && workspaceFactory is not null)
    cloneTask = StartCloneAsync(request, ct);

LanguageMetadata? language = null;
try
{
    language = await detector.ExtractMetadataAsync(reader, request.HeadSha, ct);
}
catch
{
    // Cancel the in-flight clone to avoid leaking the workspace.
    if (cloneTask is not null)
        await CancelAndDisposeCloneAsync(cloneTask);
    throw;  // rethrown; outer catch returns Empty
}

BuildResult? buildResult = null;
if (language is not null && cloneTask is not null)
    buildResult = await RunBuildOnCloneAsync(cloneTask, language.LanguageId, request.Config, ct);

return new GroundingContext(language, buildResult, null);
```

`StartCloneAsync` returns `Task<IWorkspace?>` (null on clone failure, logged as Warning). `RunBuildOnCloneAsync` awaits `cloneTask`, runs the matching runner on the ready workspace, and disposes in `finally`.

**New tests** in `ReviewBot.Grounding.Tests/CompositeGroundingProviderTests.cs` (4 cases):
- Clone starts before metadata extraction completes: use `TaskCompletionSource`-backed test doubles where `ExtractMetadataAsync` blocks until signalled; assert clone factory was called before unblocking.
- Clone failure during parallel extraction: metadata still returned; `Build` is null.
- Metadata extraction throws: workspace is disposed (verify `DisposeAsync` called on the pre-started workspace).
- Build disabled: `workspaceFactory` never called, even when a detector matches.

---

## Phase 11: Confidence scoring and comment filtering

**The problem.** Every posted comment carries the same implicit weight. A speculative style nit gets the same treatment as a confirmed null-reference bug. Reviewers either override every comment or begin to ignore the bot entirely.

**The approach.** Extend the response schema with a `confidence` field per comment. Add a configurable `min_confidence` threshold to `review-bot.yml`. The worker filters below-threshold comments before posting. This field is also foundational for Phase 14's self-critique pass, which uses confidence to decide which comments to defend.

### Step 34: Confidence field in InlineComment, parser, and prompt schema

**`ReviewBot.Core/Domain/ReviewResult.cs`:** Add `Confidence` property to `InlineComment`:

```csharp
public sealed record InlineComment(
    string Path,
    int Line,
    string Side,
    string Body,
    Severity Severity,
    Confidence Confidence = Confidence.High);

public enum Confidence { Low = 0, Medium = 1, High = 2 }
```

Default `Confidence.High` on the record parameter makes this backwards-compatible: any code constructing `InlineComment` without the new field continues to compile and behaves as if confidence is high (i.e., not filtered).

**`ReviewBot.Core/Llm/LlmResultParser.cs`:** In `TryParseComment`, parse the `confidence` field via `TryGetString(element, "confidence", out var confidence)` and map `"low"` → `Confidence.Low`, `"medium"` → `Confidence.Medium`, anything else → `Confidence.High`.

**`ReviewBot.Core/Prompting/PromptBuilder.cs`:** In `BuildSystemPrompt`, append confidence instructions before the JSON schema block:

```
Assign a confidence level to each comment based on how certain you are:
- "high": you have seen the code in question and are certain this is a real issue
- "medium": likely an issue but depends on context outside the diff
- "low": speculative or stylistic; you would not block a merge on this alone
```

Update the inline JSON schema string to include `"confidence": "high|medium|low"` as a required field in each comment object.

**Tests** in `ReviewBot.Core.Tests/`:
- `LlmResultParserTests`: response with `"confidence": "low"` → `Confidence.Low`; response without `confidence` field → defaults to `Confidence.High`.
- `PromptBuilderTests`: system prompt contains the confidence instruction text; JSON schema string contains `confidence` field.

### Step 35: Filtering pipeline and config

**`ReviewBot.Core/Domain/ReviewConfig.cs`:** Add `MinConfidence` to `ReviewOutputConfig`:

```csharp
public sealed record ReviewOutputConfig(
    bool InlineComments,
    bool Summary,
    int MaxFiles,
    int MaxPatchLines,
    TriggerConfig Trigger,
    Confidence MinConfidence = Confidence.Low);
```

Default `Confidence.Low` means no filtering — all comments pass.

**`ReviewBot.GitHub/Config/RepoConfigFetcher.cs`:** YAML DTO updated with `min_confidence` string field under `review:`. Map `"low"` / `"medium"` / `"high"` to the enum; unknown values default to `Confidence.Low` with a logged warning.

**`ReviewBot.Api/Workers/ReviewWorker.cs`:** In `ApplyOutputConfig`, apply confidence filter after the existing `InlineComments` gate:

```csharp
var comments = config.Review.InlineComments
    ? result.Comments.Where(c => c.Confidence >= config.Review.MinConfidence).ToArray()
    : Array.Empty<InlineComment>();
```

**`docs/configuration.md`:** Document `review.min_confidence` with the three accepted values and their effects.

**Tests:**
- `ReviewWorkerTests` (substituting `IReviewLlm`): `min_confidence: medium` → low-confidence comment removed, high and medium retained.
- `RepoConfigFetcherTests`: `min_confidence: high` in YAML → `Confidence.High`; missing field → `Confidence.Low`.
- `ApplyOutputConfig` unit tests (directly on the static or extracted method): all three threshold levels.

---

## Phase 12: Incremental reviews

**The problem.** When a developer pushes a fixup commit or force-pushes a rebase, the bot re-reviews the entire PR including files untouched since the last review. This wastes LLM tokens and re-surfaces already-seen feedback on unchanged lines.

**The approach.** Track the last-reviewed HEAD SHA per PR in the database. On subsequent reviews, use GitHub's compare API to restrict the file list to files changed since that SHA. If no files changed since the last review, skip the LLM call entirely.

### Step 36: PrReviewState entity and store

**`ReviewBot.Persistence/Entities/PrReviewStateRecord.cs`:** New EF entity:

```csharp
public sealed class PrReviewStateRecord
{
    public long InstallationId { get; set; }
    public string RepoFullName { get; set; } = string.Empty;
    public int PullNumber { get; set; }
    public string LastSha { get; set; } = string.Empty;
    public DateTimeOffset ReviewedAt { get; set; }
}
```

**`ReviewBot.Persistence/ReviewBotDbContext.cs`:** Add `DbSet<PrReviewStateRecord> PrReviewStates => Set<PrReviewStateRecord>();` and configure the composite PK `(InstallationId, RepoFullName, PullNumber)` in `OnModelCreating`.

**New EF migration** in `ReviewBot.Persistence/Migrations/` via `dotnet ef migrations add AddPrReviewState`.

**`ReviewBot.Core/Storage/IPrReviewStateStore.cs`:** New interface:

```csharp
public interface IPrReviewStateStore
{
    Task<string?> GetLastShaAsync(long installationId, string repoFullName, int pullNumber, CancellationToken ct);
    Task SetLastShaAsync(long installationId, string repoFullName, int pullNumber, string sha, CancellationToken ct);
}
```

**`ReviewBot.Persistence/EfCorePrReviewStateStore.cs`:** Implements `IPrReviewStateStore` using `ReviewBotDbContext`. Uses `ExecuteUpdateAsync`/upsert pattern — `FindAsync` + update or add, then `SaveChangesAsync`.

**DI:** registered in `ReviewBot.Persistence/DependencyInjection.cs` alongside `EfCoreDeliveryStore`.

**Tests** in `ReviewBot.Persistence.Tests/`: upsert then retrieve returns same SHA; different PR returns null; overwrite updates the SHA.

### Step 37: GitHub compare API integration

**`ReviewBot.GitHub/Pulls/IPullRequestFetcher.cs`:** Add a new method (or a new interface `IPullRequestComparer`):

```csharp
Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(
    string owner, string repo, string baseSha, string headSha,
    string installationToken, CancellationToken ct);
```

**Implementation** in `ReviewBot.GitHub/Pulls/PullRequestFetcher.cs` (or a dedicated `PullRequestComparer`): uses Octokit's `client.Repository.Commits.Compare(owner, repo, baseSha, headSha)`, returns `response.Files.Select(f => f.Filename).ToArray()`. Octokit's compare response caps at 300 files; log a Warning if the response is exactly 300 (may be truncated).

**Tests** in `ReviewBot.GitHub.Tests/`: substitute `IGitHubClientFactory`; assert `Compare` called with the correct SHAs; returns file name list; 404 response throws a descriptive exception.

### Step 38: Worker integration

**`ReviewBot.Api/Workers/ReviewWorker.cs`:** Inject `IPrReviewStateStore` alongside existing dependencies.

In `ProcessAsync`, after `ApplyIgnoreGlobs` and before `ApplyMaxFiles`:

1. Query `IPrReviewStateStore.GetLastShaAsync(job.InstallationId, $"{job.Owner}/{job.Repo}", job.PrNumber, ct)`.
2. If `lastSha` is non-null and differs from `snapshot.HeadSha`:
   - Call `GetChangedFilesSinceAsync(lastSha, snapshot.HeadSha)` to get the changed-file name set.
   - Filter `files` to those whose `Path` is in the changed set.
   - If the resulting file list is empty: log Debug "No files changed since last review; skipping LLM call" and call `SetLastShaAsync` (to update timestamp), then return `JobProcessStatus.Skipped`.
3. After `reviewPoster.PostAsync` completes successfully: call `SetLastShaAsync(job.InstallationId, ..., snapshot.HeadSha)`.
4. On compare API failure: log Warning, fall back to the full file list, proceed normally.

`IPrReviewStateStore` is injected (not optional) — it is always registered.

**Tests** in `ReviewBot.Api.Tests/Workers/ReviewWorkerTests.cs` (4 new cases):
- First review of a PR: SHA stored after posting.
- Second push (new SHA, files changed): compare API called with old SHA as base; only delta files in LLM request.
- Second push (new SHA, no files changed since last review): LLM not called; SHA updated; returns Skipped.
- Compare API throws: full file list used; Warning logged; review proceeds.

---

## Phase 13: E2E test infrastructure

**The problem.** No test covers the full path: webhook HTTP POST → job enqueue → background worker execution → GitHub API comment posting. Wiring bugs, middleware regressions, and cross-component interactions are invisible until production.

**The approach.** A dedicated test project hosts the full ASP.NET Core app in-process via `WebApplicationFactory<Program>`. GitHub API and LLM endpoints are replaced by `WireMock.Net` stubs. A `WorkerSyncHelper` polls for the expected GitHub API call to appear in the WireMock capture log. Scenarios are expressed as reusable fixture files.

### Step 39: E2E test project and harness

**New project** `tests/ReviewBot.E2E.Tests/ReviewBot.E2E.Tests.csproj` targeting the same TFM as `ReviewBot.Api`. Added to solution file.

**Dependencies:**
- `WireMock.Net` — in-process HTTP mock server.
- `Microsoft.AspNetCore.Mvc.Testing` — `WebApplicationFactory<Program>`.
- `xunit`, `xunit.runner.visualstudio` (already used elsewhere).

**`ReviewBotHarness`** (`IAsyncLifetime`, used as `IClassFixture`):
- Starts two `WireMockServer` instances on random ports: `GitHubMock` and `LlmMock`.
- Creates a `WebApplicationFactory<Program>` that overrides:
  - `GitHubOptions.BaseUrl` → `GitHubMock` URL.
  - `LlmBaseUrl` (Anthropic/OpenAI) → `LlmMock` URL.
  - `GitHubAppOptions.WebhookSecret` → `"test-secret"`.
  - SQLite connection string → temp file path (cleaned up in `DisposeAsync`).
- Exposes `HttpClient CreateClient()` for posting webhooks.
- Exposes `GitHubMock` and `LlmMock` for per-test stub configuration.
- `ResetAsync()` clears all WireMock stubs and captured requests between tests.

**`WebhookSender`** helper: computes `X-Hub-Signature-256` HMAC-SHA256 over the body using `"test-secret"`, posts to `/webhook` with correct `X-GitHub-Event` and `X-GitHub-Delivery` headers.

**`WorkerSyncHelper`**: polls `GitHubMock.LogEntries` in a loop (50 ms intervals, 10-second timeout) until a `POST` request matching a supplied path predicate appears. Throws `TimeoutException` with a diagnostic message on expiry. Also exposes `WaitForNoCallAsync` (asserts the predicate is never matched within 2 seconds) for negative assertions.

### Step 40: Fixture library and baseline E2E scenarios

**Fixture files** in `tests/ReviewBot.E2E.Tests/Fixtures/` (embedded resources):
- `webhook-pr-opened.json` — realistic `pull_request.opened` payload for `owner/repo` PR #1, SHA `abc123`.
- `webhook-pr-synchronize.json` — `pull_request.synchronize` payload, same PR, new SHA `def456`.
- `pr-files-dotnet.json` — GitHub `/pulls/{n}/files` response with one `.cs` file diff.
- `pr-files-empty.json` — empty files array.
- `directory-build-props.xml` — `Directory.Build.props` with `net10.0` for grounding fixture.
- `llm-response-two-comments.json` — canned LLM response: summary + two `high`-confidence comments.
- `llm-response-mixed-confidence.json` — one `high` and one `low` comment (used in Phase 11 tests).
- `installation-token-response.json` — GitHub Apps installation access token response.

**Baseline tests** in `ReviewBot.E2E.Tests/Scenarios/`:

`BasicReviewTests` (uses `ReviewBotHarness`):
- **`PrOpened_PostsTwoComments`**: stub GitHub token endpoint, stub PR files with `pr-files-dotnet.json`, stub LLM with `llm-response-two-comments.json`. Post `webhook-pr-opened.json`. Assert `WorkerSyncHelper` sees two `POST /repos/owner/repo/pulls/1/reviews` calls (or the review comments endpoint, depending on `ReviewPoster` implementation).
- **`PrOpened_Idempotent`**: deliver same webhook twice (same `X-GitHub-Delivery` ID). Assert LLM called only once and review posted only once.
- **`PrOpened_EmptyDiff`**: stub PR files with `pr-files-empty.json`. Assert `WorkerSyncHelper.WaitForNoCallAsync` confirms no LLM call, no review POST.
- **`PrOpened_AllFilesIgnored`**: repo config sets `ignore: ['**']`. Assert no LLM call.

Each test class registers a `[Collection("E2E")]` to run sequentially (WireMock port conflicts under parallel execution).

---

## Phase 14: Self-critique pass

**The problem.** A single-pass review contains false positives: code the model misreads, missing-error-handling comments where the caller handles it, and range-check complaints about values already validated upstream. A second LLM pass focused on finding false positives removes most of these at 2× the per-review LLM cost.

**The approach.** After the initial review, if `review.self_critique: true`, make a second LLM call. The critique prompt presents the diff and the numbered proposed comments and asks the model to return the indices of comments worth posting. Only retained comments are posted. Critique LLM failures fall through to the full initial comment list — a review is never blocked.

### Step 41: Self-critique prompt builder and response schema

**`ReviewBot.Core/Prompting/SelfCritiquePromptBuilder.cs`:**

```csharp
public static class SelfCritiquePromptBuilder
{
    public static PromptPayload Build(
        IReadOnlyList<FileChange> files,
        IReadOnlyList<InlineComment> proposedComments);
}
```

System prompt: instructs the model it is a senior reviewer evaluating a junior reviewer's proposed comments for accuracy and usefulness. Lists criteria for removal: comment targets a line not in the diff, claims a bug that is clearly handled elsewhere in the same diff, flagging valid modern syntax as invalid, pure style preference with no correctness implication.

User prompt: the diff (same format as the primary review), followed by the numbered proposed comments (index, path, line, body).

Response schema (separate from the primary review schema):

```json
{
  "retained_indices": [0, 2],
  "rationale": "string, brief explanation of removals"
}
```

`retained_indices` is the authoritative output. The original comment text is never re-emitted — the model only votes on indices. `rationale` is logged at Debug.

**`ReviewBot.Core/Domain/SelfCritiqueResult.cs`:**

```csharp
public sealed record SelfCritiqueResult(IReadOnlyList<int> RetainedIndices, string Rationale);
```

**`ReviewBot.Core/Llm/SelfCritiqueParser.cs`:** parses the critique JSON response, extracts `retained_indices` array. Invalid response (parse failure, missing field, out-of-range indices) returns `null` — caller falls back to all comments.

**Tests** in `ReviewBot.Core.Tests/Prompting/SelfCritiquePromptBuilderTests.cs`:
- Prompt contains the full diff and each proposed comment with its index.
- Response JSON schema contains `retained_indices` array.
- `SelfCritiqueParser`: valid response with subset of indices → correct `SelfCritiqueResult`; missing field → null; out-of-range index → null.

### Step 42: Worker integration

**`ReviewBot.Core/Domain/ReviewConfig.cs`:** Add `bool SelfCritique = false` to `ReviewOutputConfig`.

**`ReviewBot.GitHub/Config/RepoConfigFetcher.cs`:** YAML DTO updated with `self_critique` boolean under `review:`.

**`ReviewBot.Api/Workers/ReviewWorker.cs`:** In `ProcessAsync`, after `result` is obtained from `llm.ReviewAsync` and before `AppendFilesSkippedNote`, add the critique gate:

```csharp
if (config.Review.SelfCritique && result.Comments.Count > 0)
{
    var critiquePayload = SelfCritiquePromptBuilder.Build(files, result.Comments);
    try
    {
        var rawCritique = await llm.CompleteRawAsync(critiquePayload, ct);
        var critique = SelfCritiqueParser.Parse(rawCritique);
        if (critique is not null)
        {
            var retained = critique.RetainedIndices
                .Where(i => i >= 0 && i < result.Comments.Count)
                .Select(i => result.Comments[i])
                .ToArray();
            result = new ReviewResult(result.Summary, retained);
            logger.LogDebug("Self-critique retained {Retained}/{Total} comments", retained.Length, result.Comments.Count + (result.Comments.Count - retained.Length));
        }
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogWarning(ex, "Self-critique failed; using full initial comment set");
    }
}
```

`IReviewLlm` gains a `CompleteRawAsync(PromptPayload, CancellationToken)` method that returns the raw string response, used for non-standard response schemas (self-critique, later agentic context). Both `AnthropicReviewLlm` and `OpenAiReviewLlm` implement it.

**`docs/configuration.md`:** Document `review.self_critique`.

**Tests** in `ReviewBot.Api.Tests/Workers/ReviewWorkerTests.cs`:
- `SelfCritique = false`: `CompleteRawAsync` never called.
- `SelfCritique = true`, critique retains indices `[0, 2]` from a 3-comment set: only comments 0 and 2 posted.
- `SelfCritique = true`, critique fails: all 3 original comments posted; Warning logged.
- `SelfCritique = true`, empty initial comment list: `CompleteRawAsync` not called.

**E2E scenario** (extends Phase 13 harness): stub LLM primary call with `llm-response-two-comments.json`; stub second LLM call (critique) with `{"retained_indices": [0], "rationale": "comment 1 targets unchanged code"}`; assert only one comment posted.

---

## Phase 15: Agentic context fetching

**The problem.** The model reviews a diff without being able to see the types, interfaces, and base classes that changed code depends on. "This doesn't implement the interface contract" is usually a false positive because the model cannot see the interface. Fetching a small number of referenced files eliminates this class of error.

**The approach.** The initial review response may include a `context_requests` field listing files the model wants to read. The worker fetches those files via the GitHub Contents API and makes a single follow-up LLM call with the enriched context. The follow-up call produces the final comment set. Bounded to `max_context_requests` files (default 5) to prevent runaway API usage. Disabled by default.

### Step 43: Context request schema, parser, and prompt changes

**`ReviewBot.Core/Domain/ContextRequest.cs`:**

```csharp
public sealed record ContextRequest(string Path, string? Reason);
```

**`ReviewBot.Core/Domain/ReviewResult.cs`:** Extend `ReviewResult`:

```csharp
public sealed record ReviewResult(
    string Summary,
    IReadOnlyList<InlineComment> Comments,
    IReadOnlyList<ContextRequest> ContextRequests = null!);
```

Default `null!` with a null-coalescing guard in callers preserves backwards compatibility with all existing construction sites.

**`ReviewBot.Core/Llm/LlmResultParser.cs`:** In `ParseRoot`, after parsing `comments`, optionally parse `context_requests` array: each element must have a `path` string; `reason` is optional.

**`ReviewBot.Core/Prompting/PromptBuilder.cs`:** `BuildSystemPrompt` extended. When `config.Review.AgenticContext == true`, append before the response schema:

```
You may request up to {MaxContextRequests} additional files to review. Include a context_requests array in your response if you need to see referenced types, interfaces, or base classes. Only request files you are confident are relevant.
```

Update the inline JSON schema to include:

```json
"context_requests": [
  { "path": "string, repo-relative path", "reason": "optional string" }
]
```

When `AgenticContext == false`, the field is absent from the schema instruction so the model never populates it.

**`ReviewBot.Core/Prompting/PromptBuilder.cs`:** New static method:

```csharp
public static PromptPayload BuildContextEnrichedRequest(
    ReviewRequest request,
    IReadOnlyList<(string Path, string Content)> fetchedFiles);
```

Assembles the follow-up prompt: original diff + initial summary + initial comments (for continuity) + a `## Additional context` section listing each fetched file in a fenced code block. System prompt instructs the model this is the final review pass: re-emit all valid comments from the first pass plus any new ones informed by the additional context.

**Tests** in `ReviewBot.Core.Tests/`:
- `LlmResultParserTests`: response with `context_requests` → `ReviewResult.ContextRequests` populated; missing field → empty list.
- `PromptBuilderTests`: `AgenticContext = true` → schema includes `context_requests` instruction; `AgenticContext = false` → schema does not.
- `BuildContextEnrichedRequest`: prompt contains fetched file content in fenced block; omits files that were null (404).

### Step 44: Worker integration

**`ReviewBot.Core/Domain/ReviewConfig.cs`:** Add to `ReviewOutputConfig`:

```csharp
bool AgenticContext = false,
int MaxContextRequests = 5
```

**`ReviewBot.GitHub/Config/RepoConfigFetcher.cs`:** YAML keys `agentic_context` and `max_context_requests` under `review:`.

**`ReviewBot.GitHub/Pulls/IPullRequestFetcher.cs`** (or `IGitHubContentService`): add `GetFileContentsAsync(string owner, string repo, string path, string sha, string installationToken, CancellationToken ct) → Task<string?>` (returns null on 404). Octokit's `Repository.Content.GetAllContentsByRef` already used in `GitHubRepoContentReader` — extract or reuse.

**`ReviewBot.Api/Workers/ReviewWorker.cs`:** After obtaining the initial `result` from `llm.ReviewAsync` and before self-critique (if enabled), add the agentic context gate:

```csharp
if (config.Review.AgenticContext
    && result.ContextRequests is { Count: > 0 } requests)
{
    var capped = requests.Take(config.Review.MaxContextRequests).ToArray();
    if (capped.Length < requests.Count)
        logger.LogWarning("Context requests capped at {Cap}; {Total} requested", capped.Length, requests.Count);

    var fetched = new List<(string Path, string Content)>();
    foreach (var req in capped)
    {
        var content = await githubService.GetFileContentsAsync(
            job.Owner, job.Repo, req.Path, snapshot.HeadSha, installationToken.Token, ct);
        if (content is not null)
            fetched.Add((req.Path, content));
    }

    if (fetched.Count > 0)
    {
        try
        {
            var enrichedPayload = PromptBuilder.BuildContextEnrichedRequest(request, fetched);
            var enrichedRaw = await llm.CompleteRawAsync(enrichedPayload, ct);
            var enrichedParsed = LlmResultParser.Parse(enrichedRaw, logger);
            if (enrichedParsed.Success)
                result = enrichedParsed.Value!;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Agentic context second pass failed; using initial comments");
        }
    }
}
```

**`docs/configuration.md`:** Document `review.agentic_context` and `review.max_context_requests`.

**Tests** in `ReviewBot.Api.Tests/Workers/ReviewWorkerTests.cs`:
- `AgenticContext = false`: `GetFileContentsAsync` never called.
- `AgenticContext = true`, 2 requests, both fetched: second LLM call made; second-pass comments used.
- 7 requests (over cap of 5): only 5 fetched; Warning logged.
- All fetches return 404: second pass skipped; initial comments used.
- Second LLM call fails: initial comments used; Warning logged.

**E2E scenario**: stub initial LLM response with `context_requests: [{path: "src/IFoo.cs"}]`; stub GitHub Contents API for `src/IFoo.cs`; stub second LLM call returning a clean comment set. Assert second LLM call payload contains `src/IFoo.cs` content; assert posted comments come from second pass.

---

## Phase 16: Tier 3 grounding (test runners)

**The problem.** Tier 2 grounding proves the PR branch compiles. The model still cannot know whether the change breaks existing tests. A behavioral regression surfaces as "Tests: FAILED (4 failed)" in the grounding context, giving the model ground truth to mention in its summary.

**The approach.** Add `ITestRunner` alongside `IBuildRunner`. `CompositeGroundingProvider` runs the test runner (in the same workspace, after a successful build) when `grounding.tests: true`. `TestResult` is already defined in `ReviewBot.Core.Domain`. `PromptBuilder` already has a placeholder for tests in `GroundingContext`. Tests only run after a successful build — running tests on a broken workspace produces no useful signal.

### Step 45: ITestRunner interface and CompositeGroundingProvider extension

**`ReviewBot.Grounding/Build/ITestRunner.cs`:**

```csharp
public interface ITestRunner
{
    string LanguageId { get; }
    Task<TestResult> RunAsync(string workspacePath, GroundingConfig config, CancellationToken ct);
}
```

**`ReviewBot.Grounding/DependencyInjection/GroundingBuilder.cs`:** Add `AddTestRunner<T>()` extension method, mirroring `AddBuildRunner<T>()`.

**`CompositeGroundingProvider`:** New constructor parameter `IEnumerable<ITestRunner> testRunners`. The existing 4-arg public constructor gains this parameter. All internal test constructors gain it with a default empty list to preserve compatibility.

`GetContextAsync` extended: after `RunBuildAsync` completes and when `request.Config.Tests == true && buildResult?.Success == true`, call `RunTestsAsync(request, language.LanguageId, ct)`. `RunTestsAsync` follows the same exception-isolation pattern as `RunBuildAsync`: runner exceptions → `TestResult(0, 0, 0, ex.Message)`, never rethrow.

`GroundingContext` returned is now `new GroundingContext(language, buildResult, testResult)`.

**`Program.cs`:** updated (Step 48).

**Tests** in `ReviewBot.Grounding.Tests/CompositeGroundingProviderTests.cs` (4 new cases):
- Tests enabled + build success: test runner called; `TestResult` in context.
- Tests enabled + build failure: test runner not called; `Tests` is null.
- Tests disabled: test runner not called.
- Test runner throws: `Tests.Passed = 0, Failed = 0`; review proceeds.

### Step 46: .NET test runner

**`ReviewBot.Grounding/Languages/DotNet/DotNetTestRunner.cs`:** Implements `ITestRunner`. `LanguageId = "dotnet"`.

Runs `dotnet test --no-build --no-restore -c Release` in `workspacePath`. Timeout from `GroundingConfig.TestTimeoutSeconds`. Captures stdout+stderr combined.

Parses the MSBuild/VSTest summary line with a regex on the last match (multi-project solutions emit one line per project then an aggregate):

```
(?:Passed|Failed)!\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+)
```

Success when `exitCode == 0`. Output truncated to 4096 chars.

Timeout (non-cancellation): returns `new TestResult(0, 0, 0, "dotnet test timed out")`. External cancellation propagates.

**`PromptBuilder.BuildGroundingSection`** extended: when `build` is not null and `tests` is not null (parameter added), append:

```
- Tests: PASSED (42 passed, 0 failed, 3 skipped) — existing behavior confirmed
```
or:
```
- Tests: FAILED (38 passed, 4 failed) — existing behavior may have regressed
```

`BuildGroundingSection` signature becomes `BuildGroundingSection(LanguageMetadata language, BuildResult? build, TestResult? tests)`. All callers updated.

**Tests** in `ReviewBot.Grounding.Tests/Languages/DotNet/DotNetTestRunnerTests.cs`:
- Valid project with passing tests: `TestResult(Passed: N, Failed: 0, Skipped: 0)`.
- Failing test: `TestResult(Failed: 1, ...)`.
- 1-second timeout: returns result, does not throw.
- External cancellation: rethrows `OperationCanceledException`.

**Tests** in `ReviewBot.Core.Tests/Prompting/PromptBuilderTests.cs`:
- Grounding with passing tests: prompt contains "Tests: PASSED".
- Grounding with failing tests: prompt contains "Tests: FAILED".
- Grounding with null tests: tests line absent from prompt.

### Step 47: Python test runner

**`ReviewBot.Grounding/Languages/Python/PythonTestRunner.cs`:** Implements `ITestRunner`. `LanguageId = "python"`.

Checks for any of: `pytest.ini`, `pyproject.toml` (with `[tool.pytest.ini_options]` section), `setup.cfg` (with `[tool:pytest]`), `conftest.py` in `workspacePath`. If none found, returns `new TestResult(0, 0, 0, "no pytest configuration detected")` without running anything.

If detected: runs `python3 -m pytest --tb=no -q --no-header`. Parses summary line:

```
(\d+) passed(?:, (\d+) failed)?(?:, (\d+) skipped)?
```

Timeout from `GroundingConfig.TestTimeoutSeconds`. External cancellation propagates.

**Tests** in `ReviewBot.Grounding.Tests/Languages/Python/PythonTestRunnerTests.cs`:
- All passing: `TestResult(Passed: N, Failed: 0)`.
- One failing: `TestResult(Failed: 1)`.
- No pytest config: returns result with 0 counts without running pytest (no process spawned).
- Timeout: returns result, does not throw.
- External cancellation: rethrows.

### Step 48: Program.cs wiring and E2E scenario

**`ReviewBot.Api/Program.cs`:**

```csharp
builder.Services
    .AddGrounding()
    .AddLanguageDetector<DotNetLanguageDetector>()
    .AddLanguageDetector<PythonLanguageDetector>()
    .AddBuildRunner<DotNetBuildRunner>()
    .AddBuildRunner<PythonBuildRunner>()
    .AddTestRunner<DotNetTestRunner>()
    .AddTestRunner<PythonTestRunner>();
```

**E2E scenario** (extends Phase 13 harness, grounding test path):
- Stub GitHub Contents API to return `directory-build-props.xml` (net10.0); stub root tree to include `Directory.Build.props`. Build grounding is not exercised in E2E (requires a real dotnet SDK in CI).
- Assert captured LLM request body contains `## Project context` section with `C# (.NET 10.0)`.

---

## Risk register

- **Open**: Dockerfile has not been exercised with an actual `docker build`. Validate before relying on container output.
- **Open**: `Anthropic.SDK` 5.10.0 is unofficial. Confined to `AnthropicReviewLlm`; revisit if an official .NET SDK ships.
- **Open**: OpenAI-compatible providers may not all support JSON mode. Mitigated by `UseJsonMode` toggle.
- **Open**: `DeliveryStoreCleanupServiceTests.ContinuesLoopWhenCleanupFails` is flaky under parallel test execution. Pre-existing; not related to current phases.
- **Open (Phase 10)**: Parallel clone + extraction introduces a concurrent workspace lifecycle. If `ExtractMetadataAsync` throws after the clone starts, `CancelAndDisposeCloneAsync` must not swallow `OperationCanceledException` — validate this in tests.
- **Open (Phase 12)**: GitHub's compare API returns at most 300 files. PRs wider than 300 files since the last review will silently miss some files. Log a Warning at 300 and document the cap.
- **Open (Phase 15)**: `IReviewLlm.CompleteRawAsync` is a new interface method. Both `AnthropicReviewLlm` and `OpenAiReviewLlm` must implement it. `StubReviewLlm` in tests needs a stub implementation.
- **Open (Phase 16)**: Tier 3 build+test in E2E tests requires a real .NET SDK on the CI runner. Mark Tier 2/3 grounding E2E scenarios with `[Trait("Category", "RequiresSdk")]` and gate them on a CI flag.

## What is intentionally excluded

- No web UI for admin or stats.
- No multi-LLM ensemble or voting across providers.
- No support for reply threads on existing review comments.
- No GitHub Enterprise Server (only github.com).
- No fine-grained per-file model selection.
- No Tier 3 grounding for languages other than .NET and Python in v2.
- Agentic context fetching is bounded to one round of file fetching — no recursive context expansion.
