# Configuration Reference

ReviewBot reads configuration from (in priority order, highest wins):

1. Environment variables prefixed with `REVIEWBOT__` (double underscore for nesting)
2. `appsettings.{Environment}.json`
3. `appsettings.json`
4. .NET user secrets (Development environment only — `dotnet user-secrets`)

Environment variables are the recommended approach in production. The sections below list every option with its environment variable name, type, and default.

---

## GitHubApp

| Key | Env var | Type | Default | Description |
|---|---|---|---|---|
| `AppId` | `REVIEWBOT__GitHubApp__AppId` | `long` | `0` | GitHub App numeric ID from the General settings page |
| `PrivateKeyPem` | `REVIEWBOT__GitHubApp__PrivateKeyPem` | `string` | `""` | RSA private key in PEM format (PKCS#1 or PKCS#8) |

Both fields are required at startup. The host will refuse to start if either is missing or if `AppId` is zero.

---

## Webhook

| Key | Env var | Type | Default | Description |
|---|---|---|---|---|
| `Secret` | `REVIEWBOT__Webhook__Secret` | `string` | `""` | Webhook secret configured in the GitHub App settings |
| `BotSlug` | `REVIEWBOT__Webhook__BotSlug` | `string` | `""` | GitHub username of the installed bot, e.g. `reviewbot[bot]` |

Both fields are required at startup.

---

## Anthropic

Used when a repo's `.github/review-bot.yml` sets `model.provider: anthropic`.

| Key | Env var | Type | Default | Description |
|---|---|---|---|---|
| `ApiKey` | `REVIEWBOT__Anthropic__ApiKey` | `string` | `""` | Anthropic API key (`sk-ant-...`) |
| `ModelName` | `REVIEWBOT__Anthropic__ModelName` | `string` | `claude-opus-4-7` | Model to use |
| `MaxTokens` | `REVIEWBOT__Anthropic__MaxTokens` | `int` | `4096` | Maximum tokens in the LLM response |
| `Temperature` | `REVIEWBOT__Anthropic__Temperature` | `decimal` | `0.2` | Sampling temperature |

`ApiKey` is validated lazily — the host starts even with an empty key, but the first review that uses the Anthropic provider will fail.

---

## OpenAi

Used when a repo's `.github/review-bot.yml` sets `model.provider: openai`. Supports any OpenAI-compatible endpoint.

| Key | Env var | Type | Default | Description |
|---|---|---|---|---|
| `ApiKey` | `REVIEWBOT__OpenAi__ApiKey` | `string` | `""` | API key (`sk-...` for OpenAI; required but may be ignored by local providers) |
| `ModelName` | `REVIEWBOT__OpenAi__ModelName` | `string` | `gpt-5.1` | Model to use |
| `BaseUrl` | `REVIEWBOT__OpenAi__BaseUrl` | `Uri?` | `null` | Override base URL; `null` uses `api.openai.com`. Set to `http://localhost:11434/v1` for Ollama. |
| `MaxTokens` | `REVIEWBOT__OpenAi__MaxTokens` | `int` | `4096` | Maximum tokens in the LLM response |
| `Temperature` | `REVIEWBOT__OpenAi__Temperature` | `float` | `0.2` | Sampling temperature |
| `ResponseFormat` | `REVIEWBOT__OpenAi__ResponseFormat` | `json_object \| json_schema \| text` | `json_object` | Controls the chat completions `response_format`. Use `text` for providers that do not support JSON mode. |

---

## Persistence

| Key | Env var | Type | Default | Description |
|---|---|---|---|---|
| `ConnectionString` | `REVIEWBOT__Persistence__ConnectionString` | `string` | `Data Source=reviewbot.db` | EF Core SQLite connection string |

The SQLite database file is created automatically on first start; no manual schema setup is required. See [Running migrations](#running-migrations) below.

---

## Worker

| Key | Env var | Type | Default | Description |
|---|---|---|---|---|
| `Concurrency` | `REVIEWBOT__Worker__Concurrency` | `int` | `1` | Number of jobs processed in parallel. The installation-token cache uses per-installation semaphores, so it is concurrency-safe. Raise only if reviews are queueing up and you have sufficient LLM rate limits. |

---

## Per-repo YAML reference

Place `.github/review-bot.yml` (or `.github/review-bot.yaml`) in any repository where ReviewBot is installed. Missing fields fall back to the defaults shown below. Malformed YAML is logged as a warning and the default config is used so reviews are not blocked by config mistakes.

```yaml
# .github/review-bot.yml

# Set to false to disable ReviewBot for this repository without uninstalling the App.
enabled: true

model:
  # LLM provider: "anthropic" or "openai" (also covers OpenAI-compatible local/proxy providers).
  provider: anthropic

  # Model name passed to the provider API.
  name: claude-opus-4-7

review:
  # Post inline comments on individual diff lines.
  inline_comments: true

  # Post a top-level summary paragraph.
  summary: true

  # Maximum number of changed files to include in the review.
  # Files are sorted alphabetically; the first N are kept when the limit is hit.
  max_files: 50

  # Maximum diff lines per file before the patch is truncated.
  # When the total across all files exceeds max_patch_lines * 5, the smallest-diff
  # files are prioritised and the rest are dropped; a note is added to the summary.
  max_patch_lines: 1500

  # Minimum confidence for inline comments to be posted: low, medium, or high.
  # Default: low (no confidence-based filtering).
  min_confidence: low

  # Run a second LLM pass over surviving low/medium-confidence comments to remove
  # likely false positives. High-confidence comments are retained without critique.
  # Default: false
  self_critique: false

  # Allow the first LLM pass to request a small number of additional repo files.
  # The worker validates paths, applies ignore globs, rejects secret-looking files,
  # fetches text files only, then runs one final LLM pass with the added context.
  # Default: false
  agentic_context: false

  # Maximum number of validated context files to fetch for the second pass.
  max_context_requests: 5

  # Maximum decoded size, in bytes, for each fetched context file.
  max_context_file_bytes: 50000

  # Fetch and include full file contents for changed, non-deleted files whose
  # patch is at or below this byte threshold. This can reduce false positives
  # for small files but increases prompt size; tune it to the model context window.
  # 0 disables full-file context.
  # Default: 0
  full_file_max_bytes: 0

  # Post REQUEST_CHANGES instead of COMMENT when any surviving inline comment
  # has severity: error. This can block merges on protected branches.
  # Default: false
  request_changes_on_error: false

  # Post APPROVE instead of COMMENT when no inline comments survive filtering.
  # Default: false
  approve_if_clean: false

  trigger:
    # Review when the bot is explicitly added as a reviewer.
    on_review_request: true

    # Review on every push to the PR branch.
    on_push: false

# Glob patterns for files to exclude from the review.
# Uses Microsoft.Extensions.FileSystemGlobbing syntax (forward slashes, ** for any depth).
ignore:
  - "**/*.generated.cs"
  - "**/*.Designer.cs"
  - "migrations/**"
  - "docs/**"

# Areas the LLM should focus on (free text; included verbatim in the system prompt).
focus:
  - correctness
  - security
  - concurrency
  - error_handling

# Free-form instructions appended to the system prompt verbatim.
# Use this for project-specific conventions, languages, or frameworks.
instructions: |
  This is a Go service. Flag unsafe pointer casts and context misuse.
  Do not comment on formatting — it is enforced by the CI linter.

grounding:
  # Tier 1: read project config files via the GitHub Contents API and inject
  # verified language/toolchain facts into the review prompt. Adds one extra
  # GitHub API call per review; no cloning required.
  # Default: true
  enabled: true

  # Tier 2: clone the PR branch and run a build or type-check command.
  # Proves that changed files compile; the result is included in the prompt.
  # Adds clone + build time to review latency — typically 30–120 s for .NET,
  # less for Python type-checking. Read the security note below before enabling.
  # Default: false
  build: false

  # Override the build command. null = auto-detect from language detector
  # (.NET → dotnet build; Python → mypy when configured, else compileall).
  # Parsed into argv directly; shell operators and environment expansion are not
  # evaluated. Use quotes for arguments containing spaces.
  # Default: null (auto-detect)
  # build_command: "dotnet build --no-restore -c Release"

  # Timeout in seconds for the build command. Reviews proceed (without build
  # grounding) if the timeout expires.
  # Default: 120
  build_timeout_seconds: 120

  # Tier 3: read completed GitHub Checks and commit statuses for the PR head SHA.
  # Does not clone the repo or execute project code. If completed checks/statuses
  # exist, their aggregate result is included in the review prompt.
  # Default: false
  tests: false

  # Tier 3 local execution for supported language runners (.NET and Python).
  # When enabled, ReviewBot runs local tests after a successful local build:
  # .NET -> dotnet test --no-build --no-restore -c Release
  # Python -> python3 -m pytest --tb=no -q --no-header when pytest config exists
  # This executes project code and should use the same security posture as build.
  # Setting local_tests: true implies tests: true.
  # Default: false
  local_tests: false

  # Override the local test command. null = auto-detect from language detector.
  # Parsed into argv directly; shell operators and environment expansion are not
  # evaluated. Use quotes for arguments containing spaces.
  # Default: null (auto-detect)
  # test_command: "dotnet test"

  # Applies only to local test runners, not GitHub Checks/status fetching.
  # test_timeout_seconds: 300
```

---

## Tier 2 build and local-test grounding — security and deployment

Setting `grounding.build: true` causes ReviewBot to clone the PR branch into a temporary directory on the worker host and run a build or type-check command against it. Local test runners behind `grounding.local_tests: true` execute test commands after a successful local build. These options are intentionally opt-in because **they execute arbitrary project code** on the host running ReviewBot.

**Threat model:** a malicious contributor could craft a PR that runs code at build time — via MSBuild targets, Python `setup.py` hooks, or similar mechanisms — if `build: true` is enabled for that repository.

**Mitigations before enabling:**

1. **Run the worker in a resource-limited container.** Docker Compose example:

   ```yaml
   services:
     reviewbot:
       image: ghcr.io/your-org/reviewbot:latest
       deploy:
         resources:
           limits:
             cpus: "1"
             memory: 2G
       security_opt:
         - no-new-privileges:true
       read_only: true
       tmpfs:
         - /tmp          # workspace clones land here; wiped on container restart
       cap_drop:
         - ALL
   ```

2. **Use a dedicated worker host** that is not shared with other sensitive services. The cloned workspace is deleted after each review, but a container with `tmpfs` for `/tmp` avoids leaving artifacts on the host filesystem.

3. **Enable only for trusted repositories.** Because `grounding.build: true` is set per repository via `.github/review-bot.yml`, you control which repos opt in. Avoid enabling it for repos that accept untrusted external PRs unless you have container isolation in place.

4. **Set a reasonable `build_timeout_seconds`.** The default is 120 s. A runaway build is cancelled and the review proceeds without build grounding — ReviewBot is never blocked by a slow or infinite build.

ReviewBot scrubs the installation token from all git error output before surfacing it in logs. The workspace temp directory is always deleted in a `finally` block, including on clone failure.

---

## Running migrations

EF Core migrations run automatically at startup. To run them manually (e.g. when preparing a new environment):

```bash
dotnet ef database update \
  --project src/ReviewBot.Persistence \
  --startup-project src/ReviewBot.Api
```

To generate a new migration after modifying `ReviewBotDbContext`:

```bash
dotnet ef migrations add YourMigrationName \
  --project src/ReviewBot.Persistence \
  --startup-project src/ReviewBot.Api \
  --output-dir Migrations
```

---

## Swapping SQLite for PostgreSQL

ReviewBot is designed so the provider swap is minimal:

1. Add the Npgsql provider to `ReviewBot.Persistence`:
   ```bash
   dotnet add src/ReviewBot.Persistence package Npgsql.EntityFrameworkCore.PostgreSQL
   ```

2. In `Program.cs`, replace `UseSqlite` with `UseNpgsql`:
   ```csharp
   builder.Services.AddReviewBotPersistence(options =>
       options.UseNpgsql(persistenceOptions.ConnectionString));
   ```

3. In `EfCoreDeliveryStore.TryRecordAsync`, add the Postgres branch next to the SQLite branch:
   ```csharp
   var sql = db.Database.IsSqlite()
       ? "INSERT OR IGNORE INTO \"Deliveries\" ..."
       : "INSERT INTO \"Deliveries\" ... ON CONFLICT DO NOTHING";
   ```

4. Create a fresh migrations folder targeting Postgres and run `dotnet ef database update`.

No other code changes are required — `IDeliveryStore` is the only persistence seam used outside `ReviewBot.Persistence`.
