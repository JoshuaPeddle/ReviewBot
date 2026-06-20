# ReviewBot Moat Plan — Verified, Self-Hosted Code Review

> Status: design draft. North star: **the only PR reviewer that runs your build, proves its findings,
> learns your repo, and shows its work — on your own hardware.**

The cloud incumbents (Copilot review, Cursor Bugbot, CodeRabbit, Greptile, Graphite) are all
opinion-based: an LLM reads the diff and guesses. Their universal failure mode is **noise** —
confident false positives. None will spin up *your* toolchain on *your* secrets to check whether the
bug they claim is real. ReviewBot already controls a runtime (`CompositeGroundingProvider` clones,
builds, tests). This plan presses that one advantage they cannot copy.

Five bets, in moat-priority order. Bets 1 and 3 are load-bearing: 1 is the moat, 3 makes it
addressable beyond .NET.

---

## Bet 0 (foundation) — Share one workspace per job

**Why first:** verification (Bet 1) needs a live clone *after* the LLM call. Today grounding clones
and disposes the workspace inside `RunBuildAndMaybeTestsOnCloneAsync`'s `finally`
(`CompositeGroundingProvider.cs:287`), and retrieval clones *again* in
`EnsureRepositoryIndexedAsync` (`ReviewWorker.cs:1246`). Two clones per review, neither survives to
the post-LLM stage.

**Design.** Introduce a job-scoped workspace cache, keyed by `(owner, repo, headSha)`:

- New `IJobWorkspaceLease` / `IJobWorkspaceProvider` in `ReviewBot.Grounding.Workspace`:
  `Task<IWorkspace> AcquireAsync(WorkspaceRequest, ct)` returns a ref-counted handle; disposed once
  when the job ends, not per-stage.
- `ReviewWorker.ProcessAsync` owns the lease for the whole job (`await using`) and passes it to
  grounding, retrieval indexing, and the new verifier.
- `CompositeGroundingProvider` and `EnsureRepositoryIndexedAsync` take the shared workspace instead
  of cloning. Keep their internal clone path as a fallback when no lease is supplied (preserves the
  existing unit-test constructors).

**Tests:** clone happens once across grounding+retrieval+verification; lease disposed exactly once;
fallback path still works when grounding runs standalone.

**Risk:** grounding currently runs *before* the LLM and disposes early to free disk under
`Worker__Concurrency`. Keep disk bounded by capping concurrent leases and reusing the existing
`MaxDiskMb`-style guard. Low risk; pure refactor with no behavior change.

---

## Bet 1 — Active verification: prove findings, don't guess them (THE MOAT)

Turn the self-critique seam (`ApplySelfCritiqueWithDropsAsync`, `ReviewWorker.cs:1954`) from "a second
LLM opinion" into "a compiler/analyzer as the second opinion." Every `error`-severity, high-confidence
finding is checked against ground truth before it posts.

**New stage.** `ReviewBot.Verification` project, `IFindingVerifier`:

```csharp
public interface IFindingVerifier
{
    Task<VerificationOutcome> VerifyAsync(
        InlineComment finding, IWorkspace workspace, GroundingContext grounding, CancellationToken ct);
}

public enum VerificationStatus { Verified, Refuted, Unverified }
public sealed record VerificationOutcome(VerificationStatus Status, string? Evidence, string Tier);
```

Runs in the worker right after filtering, in parallel with / replacing the speculative self-critique
for the findings it can adjudicate. Map onto the existing drop machinery:
`Refuted` → `DroppedComment(comment, "verification_refuted")`; `Verified`/`Unverified` → kept, with
`Verification` recorded on the comment.

**Tier ladder** (each tier is language-pluggable via the existing `IBuildRunner`/runner pattern):

- **V0 — build refutes compile claims.** *Already shipped* (`ClaimsCompileFailureContradictedByBuild`,
  `ReviewWorker.cs:2105`). Fold into the verifier as tier 0.
- **V1 — diagnostics corroborate (first slice).** Run the language's analyzer/type-checker on the head
  workspace, scoped to changed files: Roslyn analyzers (.NET), `tsc --noEmit` + eslint (TS/JS),
  `mypy`/`ruff` (Python), `go vet` (Go). A real diagnostic at/near the finding's line *corroborates*
  (→ `Verified`); an `error`-claim of a class the analyzer fully covers with no matching diagnostic is
  *refuted* (→ drop). Reuses the build-runner process model (`ProcessCommand`).
- **V2 — fix compiles (second slice).** Apply the finding's suggestion block (Bet 5, an exact
  replacement) to the workspace file and re-run the build runner. Compiles → attach "✓ fix verified to
  build." Pairs directly with Bet 5; reuses `IBuildRunner` as-is.
- **V3 — reproduction (flagged, later).** Ask the model for a minimal failing test, run it, confirm
  red on head and green with the fix. Highest trust, highest flake risk — behind
  `review.verification.reproduce: false` by default.

**Config** (`ReviewOutputConfig`): `VerificationConfig { bool Enabled, string MaxTier ("v1"),
bool Reproduce }`. Default V1 on for languages with an analyzer; no-op otherwise (→ `Unverified`,
current behavior preserved).

**Domain change.** Add `VerificationStatus? Verification` to `InlineComment`
(`ReviewResult.cs:26`). Surface a `✓ Verified` badge + evidence in the posted body (Bet 4).

**Eval gate.** New fixtures: (a) plausible-but-false `error` claim that diagnostics refute → must be
dropped; (b) true claim diagnostics corroborate → must survive with `Verified`. Measure
false-positive reduction with **no true-positive loss** vs the reference model. Dogfood loop required.

**Risks:** analyzer runtime cost (cap by changed-file scope + timeout, reuse grounding timeouts);
analyzer absence must be a clean no-op, never a refute; flaky reproduction kept behind a flag.

---

## Bet 2 — Learn the repo over time (the persistence moat)

A reviewer that gets *quieter and sharper* the longer it runs on your repo is a moat no per-customer
SaaS can match. We already persist via EF Core (`ReviewBotDbContext`, 2 tables) and write
candidate/posted/dropped traces.

**2a — Capture outcomes.** New table `ReviewFindingRecord` (installationId, repoFullName, prNumber,
headSha, path, line, bodyHash, severity, confidence, verification, category, postedAt, outcome).
Capture the outcome signal from GitHub:

- Subscribe to `pull_request_review_thread` (resolved/unresolved) and
  `pull_request_review_comment` (deleted) in `WebhookEndpoint`.
- On re-review, reconcile the bot's prior comments: resolved/deleted/👎 → `dismissed`; replied-and-fixed
  / line changed in the direction suggested → `accepted`.
- New `IReviewFindingStore` in `ReviewBot.Persistence` + migration.

**2b — Aggregate into per-repo calibration.** A `category` tag per finding (derive from focus area +
a lightweight rule/classifier). Aggregate accepted-vs-dismissed rates per category per repo into
`IRepoCalibrationStore`.

**2c — Inject calibration.** In `PromptBuilder.BuildSystemPrompt`, append a short "house calibration"
block ("in this repo, `X`-type comments are usually dismissed — raise the bar") plus a few-shot of
recent accepted/dismissed examples. Token-budgeted like every other section.

**Eval gate:** simulate a repo with a known dismissal pattern; confirm the dismissed category's volume
drops on the next review without suppressing a held-out true finding.

**Risks:** outcome inference is noisy (a closed thread ≠ agreement) — start with the highest-precision
signals only (explicit 👎, deleted comment, resolved-with-matching-code-change); require a minimum
sample size before calibration activates. Largest epic; runs as its own track.

---

## Bet 3 — Polyglot, or "beat Copilot" isn't true

Today grounding detects only `dotnet`/`python` (`Languages/DotNet`, `Languages/Python`) and retrieval
parses **only C#** (`CSharpRepoSymbolParser`). Copilot is polyglot.

**3a — Grounding languages (low risk, additive).** Add `ILanguageDetector` + `IBuildRunner` +
`ITestRunner` for Node (npm/pnpm + jest/vitest), Go (`go build`/`go test`), Java (gradle/maven). The DI
lists are already pluggable (`GroundingServiceCollectionExtensions`); each language is a self-contained
add with no changes to the spine.

**3b — Tree-sitter retrieval (the unlock).** Replace the lexical C# parser behind the existing
`IRepoSymbolParser` / `IDiffSymbolExtractor` seams with `TreeSitterRepoSymbolParser` +
`TreeSitterDiffSymbolExtractor`, selecting a grammar by file extension (C#, TS/JS, Python, Go, Java,
Rust). One integration → symbol index for ~every language.

**Risk:** tree-sitter ships native grammars — must work inside the self-hosted container. De-risk with a
spike: bundle prebuilt grammar binaries in the image, fall back to the lexical C# parser if the native
load fails. Keep C# on the existing parser until tree-sitter reaches parity in evals.

---

## Bet 4 — Explainable review (trace → product)

We already capture *why* every comment survived or died. Surface it so reviewers trust the bot.

**Design.** Add an `Evidence` field to `InlineComment`, populated by the retrieval stage (the snippet
that grounds the finding) and the verifier (Bet 1 result). In `ReviewPoster.BuildPayload`
(`ReviewPoster.cs:127`), append a collapsed `<details>Evidence</details>` block to `comment.Body`:
the corroborating diagnostic / retrieved definition / "✓ fix verified to build." Optionally link the
per-review JSON trace. Mostly plumbing existing data into the posted body. Low risk, high trust.

---

## Bet 5 — Verified suggestion blocks

Copilot's best UX is one-click suggestions. The prompt already permits exact-replacement suggestion
blocks; make them structured and verifiable.

**Design.**
- Add `suggested_replacement` (start line, end line, replacement text) per comment to the review JSON
  schema (`PromptBuilder`, `ReviewJsonSchema`, `LlmResultParser`).
- Carry it on `InlineComment`; render as a ```suggestion fence in `ReviewPoster`.
- Gate a "✓ Verified fix" badge on Bet 1 V2 (the fix compiles).

"A fix you can click to apply, that we already compiled" beats "here's a guess."

---

## Sequencing

```
Bet 0  Workspace sharing ───────────────┐ (unblocks 1; halves clone cost)
                                         ▼
Bet 1  Verification  V0→V1 ─────► V2 (needs Bet 5 suggestions)
Bet 5  Structured suggestions ──────────┘
Bet 4  Evidence in comments (incremental; consumes 1 + retrieval output)
Bet 3  Polyglot   3a grounding langs (parallel, low risk) → 3b tree-sitter (spike first)
Bet 2  Learning   2a capture → 2b aggregate → 2c inject   (own epic, parallel)
```

**Recommended first slice:** Bet 0 → Bet 1 V1. That ships the moat (refute/corroborate `error`
findings against real diagnostics) end to end and is fully gated by the existing eval harness.

## Cross-cutting workstreams

- **Domain model:** `InlineComment` grows `Verification`, `Evidence`, `SuggestedReplacement`; the
  review JSON schema + `LlmResultParser` + trace types follow.
- **Config:** new `VerificationConfig` + suggestion knobs under `ReviewOutputConfig`, documented in
  `docs/configuration.md` and `README.md`.
- **Evals + dogfood:** every bet runs through `tests/ReviewBot.Evals` and the required
  review-your-own-PR loop in `CLAUDE.md`. New fixtures for verification precision and per-repo
  calibration. Don't widen the funnel — the whole design stays pointed at *precision with proof*.

## Discovered follow-ups (from dogfooding)

- **Subject the summary to actionability + self-critique filtering.** PR #34's
  self-review showed confidently-wrong reasoning and literal think-out-loud
  ("Wait, if it's evicted…") reaching the posted *summary*, which today bypasses
  the `FilterCandidateComments` / self-critique path that inline comments go
  through. Fold the summary into that discipline (Bet 1 / Bet 4 territory).

## What we explicitly will NOT do

Chase Copilot on breadth-of-nitpicks or latency. The existing precision controls (`MinConfidence`,
self-critique, "when in doubt, omit," dropping compile claims a green build refutes) are correct.
Widening the funnel to match their comment volume throws away the only durable advantage.
