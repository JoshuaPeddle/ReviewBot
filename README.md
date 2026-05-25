# ReviewBot

A GitHub App that automatically reviews pull requests using any Anthropic or OpenAI-compatible LLM. Reviews fire when a PR is opened or updated, or on demand by commenting `/review`.

## How it works

```
GitHub ──webhook──► POST /webhook ──► Channel Queue ──► ReviewWorker
                        │                                    │
                        ▼                                    ├── RepoConfigFetcher (.github/review-bot.yml)
              Signature validation                           ├── PullRequestFetcher (Octokit)
              Idempotency check                              ├── IReviewLlm (Anthropic / OpenAI-compatible)
              Event filtering                                └── ReviewPoster (inline comments + summary)
```

1. GitHub sends a `pull_request` webhook when a PR is opened, reopened, or updated (push), or an `issue_comment` webhook when someone comments `/review`.
2. The webhook endpoint validates the HMAC signature, deduplicates deliveries, and enqueues a `ReviewJob`.
3. A background worker fetches the repo config, PR diff, and any per-repo ignore rules, then calls the configured LLM.
4. The LLM response is parsed and posted back to GitHub as a pull request review with inline comments.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A registered [GitHub App](#github-app-setup) with Contents read, Issues read, and Pull requests read/write permissions
- An API key for Anthropic **or** a compatible OpenAI endpoint (Ollama, vLLM, OpenAI, etc.)

## Quick start

```bash
git clone https://github.com/your-org/ReviewBot
cd ReviewBot

# Copy and edit the environment file (or use appsettings.Development.json / user secrets)
export REVIEWBOT__GitHubApp__AppId=12345
export REVIEWBOT__GitHubApp__PrivateKeyPem="$(cat path/to/private-key.pem)"
export REVIEWBOT__Webhook__Secret="your-webhook-secret"
export REVIEWBOT__Anthropic__ApiKey="sk-ant-..."   # or set OpenAi options

dotnet run --project src/ReviewBot.Api
```

The service starts on `http://localhost:5000` (HTTPS on 5001). Visit `/healthz` to confirm everything is wired up.

> **Local development tip:** Use [smee.io](https://smee.io) or [ngrok](https://ngrok.com) to forward GitHub webhooks to your local machine.

## Triggering reviews

ReviewBot fires automatically on three events — no manual setup needed per PR:

| Trigger | When it fires |
|---|---|
| **PR opened / reopened** | A new pull request is opened or a closed one is re-opened |
| **Push** | New commits are pushed to the PR branch (requires `on_push: true` in repo config — off by default) |
| **`/review` comment** | Anyone posts a comment containing exactly `/review` on the PR |

The review summary always ends with a reminder of the comment trigger, so contributors can re-request a review without leaving the PR page.

## Per-repo configuration

Add `.github/review-bot.yml` to your repository. All fields are optional and fall back to sensible defaults.

```yaml
enabled: true

model:
  provider: anthropic          # anthropic | openai
  name: claude-opus-4-7

review:
  inline_comments: true
  summary: true
  max_files: 50
  max_patch_lines: 1500
  trigger:
    on_review_request: true
    on_push: false

ignore:
  - "**/*.generated.cs"
  - "migrations/**"

focus:
  - correctness
  - security
  - concurrency
  - error_handling

instructions: |
  This is a Go service. Flag unsafe pointer casts and context misuse.
```

See [docs/configuration.md](docs/configuration.md) for the full per-repo YAML reference.

## Swapping LLM providers

### Anthropic (default)

```bash
export REVIEWBOT__Anthropic__ApiKey="sk-ant-..."
export REVIEWBOT__Anthropic__ModelName="claude-opus-4-7"
```

And in `.github/review-bot.yml`:
```yaml
model:
  provider: anthropic
  name: claude-opus-4-7
```

### OpenAI

```bash
export REVIEWBOT__OpenAi__ApiKey="sk-..."
export REVIEWBOT__OpenAi__ModelName="gpt-4.1"
```

```yaml
model:
  provider: openai
  name: gpt-4.1
```

### Ollama (local)

```bash
export REVIEWBOT__OpenAi__BaseUrl="http://localhost:11434/v1"
export REVIEWBOT__OpenAi__ApiKey="ollama"        # value required but ignored
export REVIEWBOT__OpenAi__ResponseFormat="text"  # Ollama may not support JSON mode
```

```yaml
model:
  provider: openai
  name: llama3.1:8b
```

### vLLM / LM Studio

Same pattern as Ollama — set `OpenAi__BaseUrl` to your server's OpenAI-compatible endpoint.

## Docker

```bash
docker build -f src/ReviewBot.Api/Dockerfile -t reviewbot .
docker run -p 8080:8080 \
  -e REVIEWBOT__GitHubApp__AppId=12345 \
  -e REVIEWBOT__GitHubApp__PrivateKeyPem="$(cat private-key.pem)" \
  -e REVIEWBOT__Webhook__Secret=your-secret \
  -e REVIEWBOT__Anthropic__ApiKey=sk-ant-... \
  reviewbot
```

## GitHub App setup

See [docs/github-app-setup.md](docs/github-app-setup.md) for a full walkthrough including required permissions and event subscriptions.

## Configuration reference

See [docs/configuration.md](docs/configuration.md) for every option, its environment variable name, and its default value.

## Troubleshooting

**401 on every webhook** — the `Webhook__Secret` in your config does not match the secret stored in the GitHub App settings.

**`/review` comment not triggering a review** — the GitHub App needs **Issues: Read** permission and must be subscribed to the **Issue comment** event. Check **App settings → Permissions & events**, then verify a delivery appears under **Advanced → Recent deliveries**.

**422 when posting a review** — GitHub rejects comments on lines that do not appear in the diff. ReviewBot filters these automatically; if you still see 422s, check the `ReviewPostException` log entry — it includes the number of accepted and dropped comments.

**"Unknown provider" error** — `model.provider` in `.github/review-bot.yml` must be `anthropic` or `openai`. Any other value falls back to the default config and logs a warning.

**High memory on large repos** — set `Worker__Concurrency=1` (the default) and reduce `review.max_files` or `review.max_patch_lines` in the repo config to control how much diff text is loaded per review.

## Running tests

```bash
dotnet test
```

## License

MIT — see [LICENSE](LICENSE).
