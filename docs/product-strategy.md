# ReviewBot Product Strategy — Break the Precision/Recall Frontier

> Status: strategy draft (2026-06-30 deep-research pass). North star: **not "an AI that
> reviews code" but "a compiler + analyzer suite that reviews code, with a local LLM as
> the explainer, triager, and prioritizer."** That is a different — and better — machine
> than any cloud incumbent ships, and it is the machine our architecture is already
> two-thirds of the way toward building.

This document is the **competitive + research layer** above the two existing execution plans.
It exists to answer one question with evidence: *what actually makes an AI code reviewer
best-in-class, and how do we get there from where the code is today?*

## Relationship to the existing plans

- [`moat-plan.md`](moat-plan.md) — the **execution bets** (workspace sharing, active
  verification tiers, repo learning, polyglot, explainable review, verified suggestions).
  It already reached this document's core thesis independently: *the moat is that we run
  the repo's real toolchain and prove findings.* This document supplies the market data and
  research evidence that plan asserts but doesn't cite, and **extends it with four net-new
  strategic ideas** (flagged `[NET-NEW]` below).
- [`development-plan.md`](development-plan.md) — **model-agnostic + context-length** work
  (detect the real window, derive budgets, strong defaults, precision eval matrix). Its
  "strong defaults" workstream has largely landed: `ReviewConfig.Default` now ships
  `SelfCritique=true`, `MinConfidence=Medium`, `Retrieval.Enabled=true` — so treat its
  "Where we are" section as partly historical.

Where this document and `moat-plan.md` disagree on sequencing, **this one wins for the
C#-first push**; they agree on everything load-bearing.

---

## 1. The market truth: one axis decides everything

Every competitor sits at a single point on the **precision/recall frontier** and defends it:

| Tool | Recall (bugs caught) | Noise | Strategy |
|---|---|---|---|
| **Greptile** | ~82% | ~11 FP/run | Whole-codebase semantic graph; tolerates noise |
| **CodeRabbit** | ~44% | ~2 FP/run | Light diff+context; "every comment worth reading" |
| **Graphite Diamond** | low | very low | Complement only — misses most critical bugs |
| **Cursor Bugbot** | narrow | low | Agentic, logic+security only, no style |
| **Qodo 2.0** | — | — | Multi-agent (bug/security/quality/test agents) → **60.1% F1, best measured** |

The reason recall and precision trade off is that **findings are ungrounded**: when the only
thing generating a finding is an LLM asked "find bugs," more recall = more hallucination. The
frontier is a *property of LLM-only review*, not a law of nature.

**You break the frontier by grounding findings in deterministic evidence.** That is the entire
strategy. Everything below serves it.

## 2. The evidence: grounding breaks the frontier

This is the strongest, most consistent result in the current literature — it is not speculative:

- A Tencent industrial study: **hybrid LLM + static analysis eliminates 94–98% of false
  positives *with high recall*** — precisely the combination the frontier says is impossible.
  ([arxiv 2601.18844](https://arxiv.org/abs/2601.18844))
- Semgrep multimodal (deterministic SAST + LLM): **3.5× more true positives at 19% lower cost**
  than AI alone. ([we45](https://www.we45.com/post/how-semgrep-combines-ai-and-static-analysis-for-smarter-security-scans))
- Static tools catch **up to 85% of LLM hallucinations**.
- LLM reviewers suffer **confirmation bias / framing effects**: telling the model "find
  security bugs" makes it *manufacture* security findings regardless of the code. Mitigations:
  neutral framing + explicit disprove-instructions. ([arxiv 2603.18740](https://arxiv.org/abs/2603.18740))

## 3. The moat: why C#-first, self-hosted wins

CodeRabbit and Greptile are language-agnostic cloud services operating on the diff plus a code
graph. They will **never** invest in deep, per-language semantic grounding for C# specifically.
We can, because we are already positioned for it:

- **We clone and build the repo** (`CompositeGroundingProvider`). CodeRabbit doesn't run your build.
- **We're a .NET app reviewing .NET code.** We can load the actual Roslyn `Compilation` and run
  the full .NET analyzer ecosystem against the user's *exact SDK and TFM* — verified diagnostics,
  real type resolution, real nullable/data-flow, a real call graph. Greptile *approximates* this
  with embeddings; we can get it *exactly* for C#.
- **Local inference is unmetered.** We can run multi-pass / agentic loops that a token-metered
  cloud tool rations.

"C# above all" is the wedge, not a limitation.

## 4. Current state (accurately grounded, 2026-06-30)

**Shipped and load-bearing — the verification pattern already works end to end:**

- `IDiagnosticProvider` seam → `FindingCorroborator` (upgrade to `Verified`) / `FindingRefuter`
  (drop compile-claims on cleanly-parsed files) → `ApplyVerificationAsync` in `ReviewWorker`.
- `RuffDiagnosticProvider` (Python) is a **working reference implementation** — build-free, runs
  over changed files, degrades to no-op when `ruff` is absent (`DiagnosticReport.ToolRan`).
- `VerificationStatus` lives on `InlineComment`; `GroundingBuilder.AddDiagnosticProvider<T>()` is
  the registration API.
- Strong precision defaults are on: `SelfCritique=true`, `MinConfidence=Medium`, speculative
  self-critique, deterministic confirmation/praise filters, deterministic findings-summary.

**The gaps that keep us on the frontier:**

1. **No C# diagnostic provider.** The flagship language has *zero* analyzer grounding. Verification
   only fires for Python, or when `grounding.build=true` (off by default). For a C#-first tool this
   is the single biggest hole.
2. **Diagnostics are used only to adjudicate, never to generate.** Today analyzer output can only
   *corroborate/refute* an LLM finding after the fact. It is never a **finding source** and never
   **seeds the prompt**. That leaves all recall on the LLM's shoulders. `[the key structural gap]`
3. **Retrieval is a regex name-matcher.** `CSharpRepoSymbolParser` matches identifier *names*, so
   `SqliteRetrievalProvider` returns *every* `Bar`, not *the* `Bar` — imprecise cross-file context.
4. **No JS/HTML/CSS lane at all.** Accessibility and front-end performance — explicit product
   targets — are invisible.
5. **The prompt triggers confirmation bias.** `PromptBuilder.BuildSystemPrompt` frames the model as
   "senior reviewer, find correctness/security/concurrency…" — the exact framing shown to manufacture
   findings, then spends ~40 lines *begging* the model not to nit.
6. **Confidence is LLM-self-reported.** `MinConfidence` filters on the model's own guess, which is
   unreliable on a 27B — instead of gating on whether evidence corroborates the finding.

---

## 5. The plan — three pillars

### Pillar 1 — Deterministic evidence (the frontier-breaker)

**1a. `RoslynDiagnosticProvider` (`LanguageId="dotnet"`).** Follow the shipped `RuffDiagnosticProvider`
pattern exactly. Open the cloned project with `MSBuildWorkspace`, get the `Compilation`, run
`.WithAnalyzers([...]).GetAnalyzerDiagnosticsAsync()`, filter to changed paths, map to `Diagnostic`.
Make it *cheap* (analyzers over changed files; no forced full-solution build) so verification runs
**by default** instead of behind `grounding.build`. Curated analyzer pack (all Roslyn NuGet):

- `SonarAnalyzer.CSharp` — bugs + security hotspots (the strongest single pack)
- `Roslynator.Analyzers` — 500+ correctness/quality rules
- `Meziantou.Analyzer` — real-world footguns (async, culture, allocations)
- `SecurityCodeScan.VS2019` — SQLi/XSS/XXE/CSRF taint patterns
- `Microsoft.VisualStudio.Threading.Analyzers` — deadlocks, sync-over-async
- `Microsoft.CodeAnalysis.NetAnalyzers` — the built-in CA rules
- `ErrorProne.NET.CoreAnalyzers` — correctness footguns

Every diagnostic is located, explained, and hallucination-free.

**1b. Flip the pipeline: diagnostics as finding-source + prompt-seed. `[NET-NEW]`** This is the
recall breakthrough the existing moat-plan does not cover. Beyond corroborate/refute:

- **Finding source** — high-signal analyzer diagnostics (security, correctness) become findings
  *directly*, so the LLM never has to "discover" them. Deterministic recall.
- **Prompt seed** — inject "verified issues on these changed lines" into the review prompt so the
  LLM *anchors on real problems and triages/explains/prioritizes them* instead of confabulating.
  LLMs are excellent at exactly this (explaining + de-duping static-analysis noise); it plays to the
  27B's strength and directly counters confirmation bias.

This is the "deterministic tools find, LLM curates" split that yields the Tencent/Semgrep results.

**1c. Accessibility + JS/CSS lane via the same seam. `[NET-NEW target]`** `EsLintDiagnosticProvider`
(or `oxlint`/`biome` for speed) bundling `typescript-eslint` + **`eslint-plugin-jsx-a11y`** +
`html-eslint` + `stylelint`; parse JSON → `Diagnostic`. Accessibility and front-end perf arrive
deterministically, for free. Note the private-feed / untrusted-build security boundary already
documented in `moat-plan.md` — prefer true parse-only tools; treat `npm install`-required linters as
build-grounding-class.

### Pillar 2 — Real semantic context (retire the regex parser; this *is* Greptile's moat)

- **C#: Roslyn semantic model retrieval.** From the same `Compilation` as Pillar 1, resolve the
  actual symbols the diff touches, then `SymbolFinder.FindReferencesAsync` for the *true* call graph,
  real definitions, interface implementations, base types, and nullable flow. Precise cross-file
  context — what lets even a 27B reason about ripple effects. Plug in behind the existing
  `IRepoSymbolParser` / `IDiffSymbolExtractor` seams; keep the regex parser as fallback until evals
  reach parity.
- **Everything else: tree-sitter** (aligns with `moat-plan.md` Bet 3b) — proper multi-language ASTs,
  structural symbol extraction for JS/TS/HTML/CSS/Python/Go in one integration.
- **Turn on the dormant embeddings lane** (`RetrievalConfig.Embeddings` flag exists) with a small
  local embedding model via the Ollama endpoint → semantic "related code" retrieval. Local, unmetered.

### Pillar 3 — Precision architecture (de-bias, gate on evidence, learn)

- **De-bias the prompt. `[NET-NEW]`** Keep the base system prompt neutral; move category-hunting into
  evidence-gated passes. Apply the confirmation-bias paper's mitigations (neutral framing +
  disprove-instructions). Directly attacks the manufactured-finding class on the 27B.
- **Evidence-weighted confidence.** Replace `MinConfidence`-on-LLM-guess with: corroborated by an
  analyzer → keep even at medium; no corroboration + no retrieval support + low confidence → drop.
  The *structural* "no nits" fix — enforce, don't plead.
- **Adversarial self-critique.** Reframe `SelfCritiquePromptBuilder` to "disprove each finding using
  only diff evidence; keep only what you cannot refute" (Chain-of-Verification). The speculative
  self-critique plumbing already exists.
- **Specialized passes (Qodo's 60.1% F1 pattern). `[NET-NEW]`** Route focused passes by file type /
  analyzer signal — security, concurrency, perf, a11y — only where relevant. Local inference is free,
  so we can afford what cloud tools ration. Reuse the parallel-chunk infra.
- **Learn from feedback** (aligns with `moat-plan.md` Bet 2; Greptile moved addressed-comment rate
  19%→55% this way). Record 👍/👎 + resolved-vs-ignored; per-repo suppression (start: category/path
  ignore lists; full: embeddings of dismissed comments → similarity filter). The compounding moat.

---

## 6. Why local Qwen-27B / 100k makes this *better*, not worse

- A 27B is weak at "find the subtle bug cold" but strong at "given this verified diagnostic + code,
  judge and explain it." The deterministic-find / LLM-curate split leans into exactly that — the
  local constraint *pushes* us toward the architecture that also wins.
- 100k context is ample; grounding on *targeted verified evidence* is more token-efficient than
  stuffing whole files, so we use the window better (and can relax the tight `MaxBodyLines=30` caps).
- Unmetered local inference → multi-pass / agentic is free for us, rationed for them.

## 7. Sequencing

```
(moat-plan Bet 0)  Workspace sharing ──────────┐  one clone survives to the post-LLM stage
                                               ▼
Pillar 1a  RoslynDiagnosticProvider ───► 1b  Diagnostics as finding-source + prompt-seed  [NET-NEW]
                                               │
Pillar 3   De-bias prompt + evidence-weighted confidence  (cheap, pairs with 1b)
                                               ▼
Pillar 1c  ESLint/axe a11y lane   →   Pillar 2  Roslyn semantic retrieval → tree-sitter → embeddings
                                               ▼
Pillar 3   Specialized passes  +  feedback learning (own epic, parallel)
```

**First slice (highest leverage): Pillar 1a → 1b.** A cheap `RoslynDiagnosticProvider` wired into
the existing verification path *and* seeding its diagnostics into the prompt. It ships verified C#
findings end-to-end, follows the proven ruff pattern, and proves the whole thesis on real output.
Dogfood it on its own PR per `CLAUDE.md` — it will immediately review itself with real diagnostics.

## 8. How we know we're winning (the flywheel)

The eval harness (`tests/ReviewBot.Evals`, live evals vs `qwen3.6-27b`) is the release gate. Add what's
missing: a **labeled precision/recall/F1 benchmark** of real PRs with known bugs (mirror the public
"4 tools / 146 PRs / 679 findings" methodology), tracked per change, gating merges on the frontier
moving up-and-left. Qodo and Greptile publish F1; we should out-publish them. Every pillar runs
through this harness — the whole design stays pointed at **precision with proof**, never funnel-widening.

## Sources

- Frontier & tool comparisons: [Greptile vs CodeRabbit recall/FP](https://techsy.io/en/blog/best-ai-code-review-tools) · [Qodo 2.0 / 60.1% F1](https://www.aikido.dev/blog/coderabbit-alternatives) · [4-tool 146-PR field study](https://dev.to/_vjk/best-ai-code-reviewer-in-2026-we-ran-4-in-parallel-for-3-weeks-146-prs-679-findings-1c0f) · [Macroscope ranking](https://macroscope.com/content/best-ai-code-review-tools-github-2026)
- Greptile architecture & learning: [Greptile Agent](https://www.greptile.com/agent) · [embedding-feedback case study](https://www.zenml.io/llmops-database/improving-ai-code-review-bot-comment-quality-through-vector-embeddings)
- Cursor Bugbot: [cursor.com/bugbot](https://cursor.com/bugbot)
- Hybrid LLM + static analysis: [Tencent FP-reduction (94–98%)](https://arxiv.org/abs/2601.18844) · [Semgrep multimodal (3.5×)](https://www.we45.com/post/how-semgrep-combines-ai-and-static-analysis-for-smarter-security-scans) · [Sourcegraph: automated review tools](https://sourcegraph.com/blog/automated-code-review-tools)
- Confirmation bias in LLM review: [arxiv 2603.18740](https://arxiv.org/abs/2603.18740)
- C# analyzers: [awesome-analyzers](https://github.com/cybermaxs/awesome-analyzers) · [MS Learn: Roslyn analyzers](https://learn.microsoft.com/en-us/visualstudio/code-quality/roslyn-analyzers-overview)
- Accessibility / front-end: [eslint-plugin-jsx-a11y](https://github.com/jsx-eslint/eslint-plugin-jsx-a11y) · [Deque axe Linter](https://www.deque.com/axe/devtools/linter/) · [web.dev a11y auditing](https://web.dev/articles/accessibility-auditing-react)
