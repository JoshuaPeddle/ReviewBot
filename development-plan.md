# ReviewBot Development Plan

## Current state (v1, 2026-05-23)

Phases 1–7 complete (22 steps), Steps 23–25 complete. The bot handles PR webhooks for GitHub Apps, reviews diffs with Anthropic or any OpenAI-compatible endpoint, posts inline comments, stores idempotency in SQLite, and is configurable per-repo via `.github/review-bot.yml`. Build green, 148 + 22 = 170 tests passing, Docker image published on tags.

Step 23 added: `ReviewBot.Grounding` class library with grounding abstractions (`GroundingContext`, `LanguageMetadata`, `BuildResult`, `TestResult`, `IGroundingProvider`, `GroundingRequest`, `ILanguageDetector`, `IRepoContentReader`); `GroundingConfig` record added to `ReviewBot.Core.Domain`; `ReviewConfig` extended with `Grounding` property; `RepoConfigFetcher` updated to parse `grounding:` YAML block with partial merge; example config updated; 5 new tests (3 in `ReviewBot.Grounding.Tests`, 2 in `ReviewBot.GitHub.Tests`).

Step 24 added: `GitHubRepoContentReader : IRepoContentReader` (uses `IGitHubClientFactory`; `ListRootFilesAsync` calls `client.Git.Tree.Get`, returns blob names; `TryReadFileAsync` calls `Repository.Content.GetAllContentsByRef`, null on 404, decodes base64); `CompositeGroundingProvider : IGroundingProvider` (first-match detector wins, silent fall-through on any exception, disabled config returns empty context immediately); `GroundingServiceCollectionExtensions.AddGrounding()` with fluent `GroundingBuilder.AddLanguageDetector<T>()`; `AssemblyInfo.cs` with `InternalsVisibleTo("ReviewBot.Grounding.Tests")` to expose internal test constructor; `Microsoft.Extensions.Logging` added to central package manifest. Design note: `CompositeGroundingProvider` uses a dual constructor pattern — public ctor takes `IGitHubClientFactory` (creates `GitHubRepoContentReader` per request via a factory lambda); internal ctor takes `IRepoContentReader` directly for test injection.

Step 25 added: `DotNetLanguageDetector : ILanguageDetector` in `ReviewBot.Grounding/Languages/DotNet/`; `CanDetect` matches `.csproj`, `.sln`, `.slnx`, `Directory.Build.props` (case-insensitive); `ExtractMetadataAsync` tries `Directory.Build.props` first, falls back to first `.csproj` in root, parses `TargetFramework`/`LangVersion`/`Nullable`/`TreatWarningsAsErrors`/`ImplicitUsings` via `System.Xml.Linq`, reads `global.json` for `sdk.version` (toolchain version), maps TFM to version string (strips `net` prefix and OS suffix), returns null when no TFM found or XML malformed; 22 new tests in `ReviewBot.Grounding.Tests/Languages/DotNet/`.

---

## Phase 8: Language grounding — Tier 1 (metadata)

**The problem.** LLMs have stale training data about language versions and cannot verify syntax without external grounding. A model trained before .NET 10 ships will reason about .NET 6 defaults and flag valid collection expressions as invalid syntax.

**The approach.** A pluggable grounding layer that extracts language metadata from the reviewed repository and injects it as verified facts into the review prompt. Three tiers:
- **Tier 1** (this phase): read project config files via the GitHub Contents API. No cloning. Minimal latency.
- **Tier 2** (Phase 9): clone the PR branch and run build/type-check commands. Proves syntax validity. User-configurable latency tradeoff.
- **Tier 3** (future): run test suite. Proves behavioral correctness.

**Design constraints:**
- Language-agnostic abstractions; concrete implementations plugged in per language.
- Tier 1 uses only the GitHub Contents API — no cloning required.
- Tier 2 config fields and interfaces are designed now so there are no breaking changes when Phase 9 ships.
- Grounding failures must never block a review; fall through silently to ungrounded mode.
- Users control the latency tradeoff via `grounding:` in `.github/review-bot.yml`.

### Step 23: Grounding abstractions and config schema

Create `src/ReviewBot.Grounding/` (new classlib, depends on Core) and `tests/ReviewBot.Grounding.Tests/`.

**Type system** in `ReviewBot.Grounding/`:

```csharp
public sealed record GroundingContext(
    LanguageMetadata? Language,
    BuildResult? Build,
    TestResult? Tests);

public sealed record LanguageMetadata(
    string LanguageId,          // "dotnet" | "python" | "typescript" | "go"
    string LanguageVersion,     // "10.0", "3.12", etc.
    string? ToolchainVersion,   // SDK/runtime version when available
    IReadOnlyList<string> Facts);

public sealed record BuildResult(bool Success, int Warnings, int Errors, string Output);

public sealed record TestResult(int Passed, int Failed, int Skipped, string Output);

public interface IGroundingProvider
{
    Task<GroundingContext> GetContextAsync(GroundingRequest request, CancellationToken ct);
}

public sealed record GroundingRequest(
    string Owner, string Repo, string HeadSha,
    string InstallationToken, GroundingConfig Config);

public interface ILanguageDetector
{
    string LanguageId { get; }
    bool CanDetect(IReadOnlyList<string> rootFileNames);
    Task<LanguageMetadata?> ExtractMetadataAsync(
        IRepoContentReader reader, string headSha, CancellationToken ct);
}

// Thin adapter over the GitHub Contents API
public interface IRepoContentReader
{
    Task<string?> TryReadFileAsync(string path, string sha, CancellationToken ct);
    Task<IReadOnlyList<string>> ListRootFilesAsync(string sha, CancellationToken ct);
}
```

**Config schema extension** in `ReviewBot.Core/Domain/ReviewConfig.cs`:

Add `GroundingConfig Grounding` property to `ReviewConfig`. New record:

```csharp
public sealed record GroundingConfig(
    bool Enabled,               // default: true
    bool Build,                 // default: false (Tier 2)
    bool Tests,                 // default: false (Tier 3)
    int BuildTimeoutSeconds,    // default: 120
    int TestTimeoutSeconds,     // default: 300
    string? BuildCommand,       // null = auto-detect
    string? TestCommand);       // null = auto-detect

public static GroundingConfig Default => new(
    Enabled: true, Build: false, Tests: false,
    BuildTimeoutSeconds: 120, TestTimeoutSeconds: 300,
    BuildCommand: null, TestCommand: null);
```

Update `ReviewConfig.Default` to include `GroundingConfig.Default`. Update `RepoConfigFetcher` YAML DTO to handle the `grounding:` block (snake_case, partial merge with defaults). Update the example config file.

**Tests** in `ReviewBot.Grounding.Tests/`:
- `GroundingConfig.Default` values are correct
- `ReviewConfig.Default` includes grounding defaults
- YAML `grounding:` block maps correctly; partial merge fills unset fields from defaults
- `GroundingContext` with all nulls is valid (no grounding available)

**Tests** in `ReviewBot.GitHub.Tests/`:
- Updated `RepoConfigFetcherTests`: grounding section in YAML maps to `GroundingConfig`

Deliverable: type system and config schema locked in. No language implementations yet, but the shape is fixed so subsequent steps never need breaking interface changes.

---

### Step 24: GitHub Contents reader and composite provider

**`GitHubRepoContentReader : IRepoContentReader`** in `ReviewBot.Grounding/Detection/`:
- Backed by `IGitHubClientFactory`
- `ListRootFilesAsync`: fetches the root tree at the given SHA via Octokit's `GitDatabase.Tree.Get`
- `TryReadFileAsync`: fetches contents via Octokit's `Repository.Content.GetAllContentsByRef`; returns null on 404; decodes base64

**`CompositeGroundingProvider : IGroundingProvider`** in `ReviewBot.Grounding/`:
- Constructor: `IReadOnlyList<ILanguageDetector> detectors`, `IRepoContentReader reader`, `ILogger<CompositeGroundingProvider>`
- If `request.Config.Grounding.Enabled == false`: return `new GroundingContext(null, null, null)` immediately
- Call `ListRootFilesAsync`; iterate detectors in registration order; first `CanDetect` match wins
- Call `ExtractMetadataAsync` on the matching detector
- On any exception: log Warning, return `GroundingContext(null, null, null)` — never rethrow
- Tier 2/3 (`Build`/`Tests` fields) remain null until Phase 9

**DI extension** `AddGrounding(IServiceCollection)`:
- Registers `GitHubRepoContentReader` as `IRepoContentReader`
- Registers `CompositeGroundingProvider` as `IGroundingProvider`
- Returns a builder so callers can chain `.AddLanguageDetector<T>()`

**Tests:**
- Reader: substitute `IGitHubClientFactory`; assert correct SHA passed to tree/content calls; not-found returns null
- Provider: two detectors registered; first match wins; second not called
- Provider: detector throws → returns empty context, does not rethrow
- Provider: grounding disabled in config → returns empty context, detectors not called
- DI registration wires correctly

Deliverable: plumbing complete. Register a detector and it works.

---

### Step 25: .NET language detector

**`DotNetLanguageDetector : ILanguageDetector`** in `ReviewBot.Grounding/Languages/DotNet/`:

`CanDetect`: true if root files contain any name ending in `.csproj`, `.sln`, `.slnx`, or equal to `Directory.Build.props`.

`ExtractMetadataAsync`:
1. Try `Directory.Build.props` at repo root; parse XML for `<TargetFramework>`, `<LangVersion>`, `<Nullable>`, `<TreatWarningsAsErrors>`
2. If not found, try the first `*.csproj` name in the root file list; parse the same properties
3. Try `global.json` at repo root; extract `sdk.version`
4. Map TFM to version: `net10.0` → `"10.0"`, `net9.0` → `"9.0"`, etc.
5. Build `Facts` list: include LangVersion if set, Nullable setting, TreatWarningsAsErrors, any `<ImplicitUsings>` setting
6. Return null if no TFM can be determined (not enough signal to ground the model)

`LanguageId = "dotnet"`.

Use `System.Xml.Linq` for XML parsing. No new package dependency.

**Tests** with inline fixture strings (raw string literals):
- `Directory.Build.props` with `net10.0` + `LangVersion=latest` → version `"10.0"`, fact includes LangVersion
- `.csproj` fallback when no `Directory.Build.props`
- `global.json` SDK version appears in `ToolchainVersion`
- Missing files → returns null (not enough signal)
- Malformed XML → returns null (caught, logged)
- `CanDetect` positive/negative cases

Deliverable: .NET repos accurately grounded with version and compiler settings.

---

### Step 26: Python language detector

**`PythonLanguageDetector : ILanguageDetector`** in `ReviewBot.Grounding/Languages/Python/`:

`CanDetect`: true if root files contain `pyproject.toml`, `setup.py`, `setup.cfg`, `requirements.txt`, or `.python-version`.

`ExtractMetadataAsync`:
1. Try `pyproject.toml` first; extract `[project] requires-python` (e.g. `">=3.12"`) and note presence of `[tool.mypy]` / `[tool.ruff]` / `[tool.pyright]` sections as type-checker facts. Parse with simple regex/string scanning — do not add a TOML library; the fields needed are simple enough. Note the exact version constraint in Facts.
2. Try `.python-version` (plain version string, e.g. `3.12.2`)
3. Try `setup.cfg` for `[options] python_requires`
4. Note major.minor as `LanguageVersion`; full string as `ToolchainVersion` when available

`LanguageId = "python"`.

**Tests** with inline fixture strings:
- `pyproject.toml` with `requires-python = ">=3.12"` → version `"3.12"`
- `pyproject.toml` with `[tool.mypy]` section → facts include mypy present
- `.python-version` fallback
- `setup.cfg` fallback
- Priority order: `pyproject.toml` wins over `.python-version`
- No Python files at all → null

Deliverable: Python repos accurately grounded.

---

### Step 27: Prompt builder grounding injection

Extend `PromptBuilder.Build`:

```csharp
public static PromptPayload Build(ReviewRequest request, GroundingContext? grounding = null)
```

If `grounding?.Language` is non-null, inject a `## Project context (verified)` section into the system prompt, placed before the response schema section:

```
## Project context (verified from repository)
- Language: C# (.NET 10.0)
- Toolchain: .NET SDK 10.0.x
- LangVersion: latest — modern C# syntax (collection expressions, primary constructors, etc.) is valid
- TreatWarningsAsErrors: true
- Build: not verified (syntax claims cannot be confirmed)
```

If `BuildResult` is also present (Tier 2):
```
- Build: SUCCESS (0 warnings, 0 errors) — all syntax in changed files is confirmed valid
```
or:
```
- Build: FAILED (3 errors) — see build output below
```

The section is explicit that facts come from project configuration, not from the model's training data.

**Tests:**
- No grounding → prompt identical to current behavior (no regression)
- .NET 10 grounding → system prompt contains version section with correct facts
- Python 3.12 grounding → system prompt contains Python version
- Grounding with build success → states syntax confirmed; with build failure → states failure

Deliverable: the model cannot claim .NET 10 is "on the roadmap" when it's told otherwise.

---

### Step 28: Worker integration and wiring

**Worker:**
- Inject `IGroundingProvider?` (null if not registered)
- After fetching PR snapshot and applying file filters, call `GetContextAsync` if provider is non-null
- Pass `GroundingContext?` through to `PromptBuilder.Build`
- Log at Debug: detected language and version; at Warning: grounding failed

**`Program.cs`:**
- Call `AddGrounding(services).AddLanguageDetector<DotNetLanguageDetector>().AddLanguageDetector<PythonLanguageDetector>()`
- `AddGrounding` is conditional on `GroundingConfig.Enabled` being bindable from options; default on

**`ReviewBot.Grounding.csproj`** added to the solution and wired:
- `ReviewBot.Api` references `ReviewBot.Grounding`
- `ReviewBot.Grounding` references `ReviewBot.Core` and `ReviewBot.GitHub` (for `IGitHubClientFactory`)

**Tests:**
- Worker: grounding provider called with correct owner/repo/sha
- Worker: grounding failure (provider returns empty context) does not fail the job
- Worker: grounding disabled in repo config → provider called but returns empty context immediately
- Smoke test in `ReviewBot.Api.Tests`: `/healthz` still returns 200 with grounding wired in

Deliverable: end-to-end Tier 1 grounding live. Model grounded in real language version from the actual project config, zero latency overhead beyond one extra GitHub Contents API call per review.

---

## Phase 9: Language grounding — Tier 2 (workspace + build)

This phase adds the workspace subsystem that clones the PR branch and runs build/type-check commands. All Tier 1 abstractions extend cleanly with no breaking changes.

### Step 29: Workspace abstraction and git clone implementation

Define in `ReviewBot.Grounding/Workspace/`:

```csharp
public interface IWorkspace : IAsyncDisposable
{
    string LocalPath { get; }
}

public interface IWorkspaceFactory
{
    Task<IWorkspace> CreateAsync(WorkspaceRequest request, CancellationToken ct);
}

public sealed record WorkspaceRequest(
    string CloneUrl, string Sha, string InstallationToken);
```

Implement `GitWorkspace`: shallow-clones the PR head SHA into a temp directory using `git clone --depth 1 --branch {sha}` (or equivalent). Cleanup deletes the directory on `DisposeAsync`.

Security note: the workspace executes arbitrary project code during build. This is opt-in (`grounding.build: true`). For multi-tenant deployments, run the worker in a container with resource limits; document this in `docs/configuration.md`.

Tests:
- Factory creates directory, LocalPath exists
- Dispose removes the directory
- Clone failure throws with useful message

### Step 30: .NET build runner

**`DotNetBuildRunner : IBuildRunner`** in `ReviewBot.Grounding/Languages/DotNet/`:
- Runs `dotnet restore --no-dependencies` then `dotnet build --no-restore -c Release --no-incremental`
- Captures stdout/stderr; respects timeout from `GroundingConfig.BuildTimeoutSeconds`
- Returns `BuildResult(Success: exitCode == 0, Warnings: count, Errors: count, Output: truncated)`
- Parses warning/error counts from MSBuild output (`Build succeeded. N Warning(s). N Error(s).`)

Tests: use a real tiny .csproj fixture written to a temp directory; assert success/failure detection and count parsing.

### Step 31: Python build runner

**`PythonBuildRunner : IBuildRunner`** in `ReviewBot.Grounding/Languages/Python/`:
- Prefers `mypy . --no-error-summary --no-color-output` if `[tool.mypy]` config detected
- Falls back to `python -m py_compile` on changed files (filenames passed via the `GroundingRequest`)
- Respects timeout; returns `BuildResult`

Tests: fixture Python files with known errors; assert error detection.

### Step 32: Workspace integration in grounding provider

Extend `CompositeGroundingProvider`:
- If `config.Grounding.Build == true` and a matching `IBuildRunner` is registered for the detected language: acquire workspace, run build, populate `BuildResult` in `GroundingContext`
- Same for Tests with `IBuildRunner` implementing a test variant (or separate `ITestRunner` interface)
- Workspace always disposed after the runner completes regardless of outcome
- Timeout honored; timeout treated as build failure (not a review failure)

Tests:
- Build enabled: workspace created, runner called, result in context
- Build disabled: workspace not created, runner not called
- Build timeout: result has `Success=false`, review proceeds

---

## Risk register

- Open: Dockerfile has not been exercised with an actual `docker build`. Validate before relying on container output.
- Open: `Anthropic.SDK` 5.10.0 is unofficial. Confined to `AnthropicSdkClient`; revisit if an official .NET SDK ships.
- Open: OpenAI-compatible providers may not all support JSON mode. Mitigated by `UseJsonMode` toggle.
- Open (Phase 9): Workspace clone executes arbitrary project code. Opt-in only (`grounding.build: true`). Recommended deployment is a resource-limited container; document in `docs/configuration.md`.

## What is intentionally NOT in v1/v2

- No web UI for admin or stats
- No multi-LLM ensemble or self-critique passes
- No support for review threads (replies to existing comments)
- No GitHub Enterprise Server (only github.com)
- No fine-grained per-file model selection
- No agentic tool-use during review (model calling tools mid-review) — Tier 1/2 grounding is pre-computed
