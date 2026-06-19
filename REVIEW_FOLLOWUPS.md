# ReviewBot — review follow-ups

Findings from the full-project audit that were not auto-fixed because they
require design decisions, breaking-change discussion, or coordinated work
across multiple files. Listed roughly by severity, then by area.

Items already fixed in the same commit (do not re-do):
- `*.pem` / `*.key` / `*.p12` / `*.pfx` now in `.gitignore` and `.dockerignore`.
- `PromptBuilder.SanitizeFetchedContent` breaks any literal ```` ``` ```` so
  attacker-controlled file content cannot close the surrounding fence.
- `PullRequestFetcher.MapStatus` no longer throws on unknown GitHub file
  statuses; defaults to `Modified` and the review proceeds.
- `AnthropicSdkClient` null-guards the fallback when `response.Message` is null.
- `WebhookEndpoint` caps body at 25 MiB (GitHub's published limit) and returns
  413 if the request lies about `Content-Length` or streams more.
- `IssueCommentEvent.CommentRef.User` is now bound; `WebhookEndpoint` rejects
  `/review` comments authored by `Webhook:BotSlug` to prevent self-trigger
  loops. A test covers the new path.
- `.github/workflows/codeql.yml` adds CodeQL analysis and a NuGet
  vulnerable-package audit on push/PR + weekly cron.

---

## 🔴 Critical

### 1. Revoke and purge the leaked GitHub App private key
`reviewbotdemo.2026-05-23.private-key.pem` was committed at repo root. The
gitignore now blocks future commits, but the key is still in `HEAD` and in git
history. Action:

1. Treat the key as compromised. Generate a new private key in the GitHub App
   settings and delete the old one.
2. Remove the file from the working tree: `git rm reviewbotdemo.2026-05-23.private-key.pem`.
3. Scrub history: `git filter-repo --invert-paths --path reviewbotdemo.2026-05-23.private-key.pem`
   (or `git filter-branch` if filter-repo isn't installed). Force-push.
4. Audit any forks / mirrors that may still carry the key.

### 2. RCE via PR-controlled `build_command` / `test_command`
`RepoConfigFetcher.FetchAsync` loads `.github/review-bot.yml` from the **head
SHA** (`src/ReviewBot.Api/Workers/ReviewWorker.cs:198`). The custom build /
test commands flow into `DotNetBuildRunner.RunConfiguredCommandAsync`,
`PythonBuildRunner.RunConfiguredCommandAsync`, and
`DotNetTestRunner.RunConfiguredCommandAsync`, which spawn them in the cloned
workspace. A fork PR can replace `build_command` with arbitrary shell and the
bot will run it.

Design choices:
- **(A)** Load `build_command` / `test_command` only from the **base ref**, never
  from PR head. Other config keys can still come from head.
- **(B)** Detect fork PRs (`pull_request.head.repo.id != pull_request.base.repo.id`)
  and disable custom commands for them.
- **(C)** Sandbox the runners (rootless container, seccomp, network egress
  blocked). Most defensive but largest project.

Recommend (A) + (B) for v1, (C) for v2. Until then, add a prominent README
note: "Do not enable this bot for repositories that accept PRs from untrusted
contributors."

### 3. Installation token leaks into git config / argv
`GitWorkspaceFactory.BuildAuthenticatedUrl`
(`src/ReviewBot.Grounding/Workspace/GitWorkspaceFactory.cs:44`) embeds the
installation token directly into the remote URL. It then sits in the cloned
workspace's `.git/config` and is visible in `ps`/`/proc/<pid>/cmdline`.
`SanitizeOutput` only masks the URL in error messages.

Fix: use an HTTP header instead. Sketch:

```csharp
var authHeader = "Authorization: Basic " +
    Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
await RunGitAsync(tempDir,
    ["-c", $"http.extraHeader={authHeader}", "fetch", "--depth", "1", "origin", sha],
    ct);
```

…and add `origin` with the plain HTTPS URL (no embedded creds). The header
still appears in `ps`, so prefer `GIT_ASKPASS` with a temporary helper script
when full hardening is needed.

---

## 🟠 High

### 4. JWT signer holds a process-wide lock around `RSA.SignData`
`GitHubAppJwtSigner.signingLock` (`src/ReviewBot.GitHub/Auth/GitHubAppJwtSigner.cs:15`)
serializes every JWT mint. The .NET docs say `RSA` instance members aren't
guaranteed thread-safe, so the lock isn't strictly wrong, but on .NET 8+ both
`RSAOpenSsl` and `RSACng` are safe in practice for `SignData`. Verify the
guarantee for the target runtime (read corefx source or test under contention)
and drop the lock. Alternative: keep one `RSA` per thread via `ThreadLocal`.

### 5. `CachingInstallationTokenProvider.locks` grows without bound
`src/ReviewBot.GitHub/Auth/CachingInstallationTokenProvider.cs:15`. A
`ConcurrentDictionary<long, SemaphoreSlim>` never sheds entries. Replace with
`IMemoryCache` storing `Lazy<Task<InstallationToken>>` keyed by installation
ID, with sliding expiration < token TTL. First caller fills the lazy; concurrent
callers `await` the same task; the cache evicts entries automatically. Pattern:

```csharp
var lazy = cache.GetOrCreate(installationId, entry =>
{
    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(50);
    return new Lazy<Task<InstallationToken>>(() => inner.GetTokenAsync(installationId, ct));
});
return await lazy.Value.ConfigureAwait(false);
```

### 6. EF Core PR-review-state upsert is TOCTOU
`EfCorePrReviewStateStore.SetLastShaAsync`
(`src/ReviewBot.Persistence/EfCorePrReviewStateStore.cs:30-51`) does
`FindAsync` then `Add`, which can race for the same PR. The second writer
throws a unique-constraint violation. Mirror the delivery store pattern
(`INSERT … ON CONFLICT (...) DO UPDATE SET ...`) via `ExecuteSqlRawAsync`.

### 7. LLM retry policy is too narrow and too short
Both `OpenAiReviewLlm.SendAsync` and `AnthropicReviewLlm.SendAsync` retry only
on `HttpRequestException` with 100ms then 200ms delays. They miss 429
throttles, transient 5xx, `TaskCanceledException`s wrapping socket timeouts,
and provider-specific exceptions. Replace the hand-rolled loop with a Polly
pipeline (`AddStandardResilienceHandler` already exists for HTTP clients) with
jittered exponential backoff and retry-after honoring.

### 8. `OpenAiSdkChatClient` allocates a `ChatClient` per call
`src/ReviewBot.Llm.OpenAi/OpenAiSdkChatClient.cs:30-33`. Cache by
`(modelName, baseUrl)`. Single owner since `OpenAiSdkChatClient` is per-DI
instance.

---

## 🟡 Medium

### 9. JSON parser defaults missing/unknown `confidence` to `High`
`LlmResultParser.ParseConfidence` (`src/ReviewBot.Core/Llm/LlmResultParser.cs:177-190`)
returns `Confidence.High` when the field is missing or unrecognized. Tests
(`tests/ReviewBot.Core.Tests/Llm/LlmResultParserTests.cs:219-238, 312-332`)
encode this contract. Changing to `Medium` would tighten safety but moves the
eval gate — coordinate with eval owners before flipping.

### 10. JSON repair pass discards the original review prompt
Both providers' `BuildRepairPrompt` send only the failed response back to the
model with a "your response wasn't JSON" instruction. The model loses every
file/diff it was supposed to review. The repair branch tends to emit empty or
placeholder JSON. Either include the original prompt + failed response +
schema in the repair user message, or remove the repair pass entirely and rely
on response-format constraints (already on by default for OpenAI/Anthropic).

### 11. Trace cleanup loads all file metadata into memory
`TraceCleanupService.RunCleanup`
(`src/ReviewBot.Api/Tracing/TraceCleanupService.cs:50-65`) materializes a
`List<FileInfo>` for every JSON under `TracesDir`. For 100k traces this is
multi-MB and slow. Shard by date (e.g. `tracesDir/yyyy-MM-dd/...`) and walk
shard-by-shard, or use a heap to keep only the N largest while iterating.

### 12. CSharpRepoSymbolParser is line-based regex
`src/ReviewBot.Retrieval/Indexing/CSharpRepoSymbolParser.cs` ships with
documented limitations (multi-line attribute lists, generics with nested `<>`,
expression-bodied properties, file-scoped namespaces edge cases). Switch to
Roslyn — you're a .NET shop, Roslyn ships with the SDK, and it would delete
hundreds of lines of regex and bring correctness. Acknowledge this is a v2
project, not a v1 fix.

### 13. Speculative-language heuristic over-triggers on `" if "`
`ReviewWorker.SpeculativeLanguagePhrases`
(`src/ReviewBot.Api/Workers/ReviewWorker.cs:2182-2189`) lists `" if "` and
`" might "` among others. The AND with
`MissingContractDirectivePhrases` and `UnseenContractPhrases` is narrow but
still produces false positives on legitimate conditional reasoning. Add unit
tests over a real-comment corpus and tune the phrase list, or replace with a
classifier prompt to the model.

### 14. Eval fixture names leak into the model prompt
`LiveEvalRunner.BuildRequestAsync`
(`tests/ReviewBot.Evals/LiveEvalRunner.cs:194-201`) sets `PrTitle =
fixture.Metadata.Name`. A fixture named "hardcoded-api-secret" primes the
model. Use neutral PR titles and bodies; the test name should be the fixture
directory name only, not the prompt content. (This is also in `MEMORY.md`.)

### 15. Defense-in-depth path validation for trace files
`JsonReviewTraceWriter.WriteAsync`
(`src/ReviewBot.Api/Tracing/JsonReviewTraceWriter.cs:36-39`) joins
`trace.Owner` / `trace.Repo` / `trace.DeliveryId` into a filesystem path.
GitHub's login and repo rules disallow `..` and `/`, and DeliveryId is a
GitHub-generated UUID, so practical exploitation requires a GitHub-side bug.
Still cheap insurance: validate or `Path.GetInvalidFileNameChars`-strip the
three segments before joining.

### 16. `ParseConfidence`-style "RawLlmResponse = firstResponse" on repair path
`OpenAiReviewLlm.ReviewAsync:74` and `AnthropicReviewLlm.ReviewAsync:75`
return `firstResponse` (the failed one) as the raw response even when the
repair pass succeeded. Tracing then misleads debuggers. Surface both
(`RawLlmResponse`, `RawLlmRepairResponse`) or change to the repaired one.

### 17. Delivery-store retention is hardcoded to 30 days
`DeliveryStoreCleanupService.ExecuteAsync`
(`src/ReviewBot.Persistence/DeliveryStoreCleanupService.cs:21`) uses
`AddDays(-30)` as a literal. Make retention and the loop interval configurable
on `PersistenceOptions` (or a new `IdempotencyOptions`).

### 18. PR is closed/merged check missing
`WebhookEndpoint.HandlePullRequestAsync` and `HandleIssueCommentAsync` accept
events without checking PR state. A `/review` on a closed PR still enqueues a
job that fetches metadata only to find nothing to review. Either filter in the
endpoint (need extra payload fields) or in `ReviewWorker.ProcessAsync` after
the metadata fetch. Latter is simpler; just bail early if `pullRequest.State
!= "open"`.

---

## 🟢 Low / nit

- `appsettings.Development.json` likely still mentions `BotSlug` — fine, it's
  used now. Just confirm dev defaults match prod expectations.
- `DotNetBuildRunner._logger` uses underscore prefix; codebase convention is
  `logger`. Rename for consistency.
- `AverageBytesPerToken = 3d` is duplicated in `SqliteRetrievalProvider` and
  `HeuristicTokenEstimator`. Pull into a shared constant under
  `ReviewBot.Core.Context`.
- `PullRequestFetcher.MaxParallelContentFetches = 3` is hardcoded — expose
  via config if downstream provider quotas warrant tuning.
- `ProcessCommand.TryParse` silently swallows `\` between non-special chars
  (`"\foo"` → `\foo`), diverging from POSIX semantics. Document or fix.
- `OctokitRateLimitRetry.MaxRetryAttempts = 1` only retries `RateLimitExceededException`.
  Add an `AbuseException` (secondary-limit) handler and a single retry on 5xx.
- Webhook endpoint has no rate limiting. Add ASP.NET Core rate-limiting
  middleware scoped to `/webhook` (low priority since signature validation
  already gates most abuse).
- `SqliteRepoIndex.DeleteUnusedBeforeAsync` returns the sum of two
  `ExecuteNonQueryAsync` calls across two tables. The number is operationally
  misleading; log per-table counts instead of returning the sum.
- `dotnet test` in `ci.yml` runs the E2E project that self-skips when env is
  missing. Skip-vs-execute is fine, but trim CI time by adding
  `--filter "FullyQualifiedName!~E2eTests"` (or `dotnet test ReviewBot.sln
  --filter Category!=E2E` once the projects are tagged).
