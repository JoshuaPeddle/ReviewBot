# GitHub App Setup

This guide walks you through registering a GitHub App for ReviewBot, configuring its permissions, and installing it on a repository or organisation.

## 1. Create the App

1. Go to **Settings → Developer settings → GitHub Apps → New GitHub App**
   - Organisation apps: **Organisation settings → Developer settings → GitHub Apps → New GitHub App**

2. Fill in the registration form:

   | Field | Value |
   |---|---|
   | **GitHub App name** | `ReviewBot` (or any unique name) |
   | **Homepage URL** | Your deployment URL, e.g. `https://reviewbot.example.com` |
   | **Webhook URL** | `https://reviewbot.example.com/webhook` |
   | **Webhook secret** | Generate a strong random value and keep a copy — you will set this as `Webhook__Secret` |

3. Under **Identifying and authorizing users**, leave all options at their defaults (no OAuth is needed).

## 2. Set permissions

Under **Repository permissions**, grant:

| Permission | Level |
|---|---|
| **Contents** | Read |
| **Metadata** | Read (automatically selected) |
| **Pull requests** | Read & write |

No other permissions are required.

## 3. Subscribe to events

Under **Subscribe to events**, tick:

- **Pull request**

That is the only event ReviewBot processes.

## 4. Create the App

Click **Create GitHub App**. GitHub will redirect you to the app's General settings page.

## 5. Note your App ID

On the General tab, copy the **App ID** integer (e.g. `12345`). This becomes `GitHubApp__AppId`.

## 6. Generate a private key

Scroll down to **Private keys** and click **Generate a private key**. GitHub downloads a `.pem` file.

Keep this file safe — it lets the bot authenticate as the App. Set its contents as `GitHubApp__PrivateKeyPem`.

```bash
# Paste the multi-line PEM as an environment variable (bash)
export REVIEWBOT__GitHubApp__PrivateKeyPem="$(cat your-app.YYYY-MM-DD.private-key.pem)"
```

> On Linux/macOS the PEM contains literal newlines. Quoting it with `$(cat ...)` preserves them. In Docker or Kubernetes, inject the file as a secret mounted at a known path and read it into the env var at container startup, or use a secrets manager.

## 7. Install the App

1. In the left sidebar of your app's settings page, click **Install App**.
2. Choose your personal account or an organisation.
3. Select **Only select repositories** and pick the repos you want ReviewBot to monitor, or choose **All repositories**.
4. Click **Install**.

GitHub will show you the installation ID in the URL: `github.com/settings/installations/INSTALLATION_ID`. ReviewBot resolves this automatically from incoming webhook payloads — you do not need to configure it manually.

## 8. Verify the bot slug

After installing, the bot appears as a user named `your-app-name[bot]` on GitHub. This is the value you set as `Webhook__BotSlug`. You can confirm it by:

1. Opening any PR in an installed repo.
2. Clicking **Reviewers → Search**.
3. Typing the app name — the suggestion shows the exact slug (e.g. `reviewbot[bot]`).

## 9. Confirm the webhook is firing

After assigning the bot as a reviewer on a PR, check **Settings → Developer settings → GitHub Apps → (your app) → Advanced → Recent deliveries**. You should see a `pull_request` event with a `200` or `202` response. If the response is `401`, the webhook secret does not match.
