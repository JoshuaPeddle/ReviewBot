#!/usr/bin/env bash
# Read back what ReviewBot posted for a PR, plus the local per-review trace
# (candidate vs posted comments, drop reasons, token usage, cost) — the trace
# is what you use to judge ReviewBot itself, not just the code under review.
#
#   scripts/read-review.sh <pr-number>
#
set -euo pipefail
cd "$(dirname "$0")/.."
set -a; . ./.env.local; set +a

PR="${1:?usage: read-review.sh <pr-number>}"
OWNER="${REVIEWBOT_OWNER:?}"; REPO="${REVIEWBOT_REPO:?}"
# GitHub returns the App's bot login lower-cased (e.g. "reviewbotdemo[bot]")
# regardless of the configured slug casing, so match case-insensitively.
BOT="$(printf '%s' "${REVIEWBOT__Webhook__BotSlug:-ReviewBotDemo[bot]}" | tr '[:upper:]' '[:lower:]')"

echo "=== Review summaries posted by $BOT to $OWNER/$REPO#$PR (newest last) ==="
gh api "repos/$OWNER/$REPO/pulls/$PR/reviews" --paginate \
  --jq ".[] | select((.user.login|ascii_downcase)==\"$BOT\") | \"[\(.state)] \(.submitted_at)\n\(.body)\n\""

echo "=== Inline comments ==="
gh api "repos/$OWNER/$REPO/pulls/$PR/comments" --paginate \
  --jq ".[] | select((.user.login|ascii_downcase)==\"$BOT\") | \"\(.path):\(.line)  \(.body)\""

echo "=== Latest local trace (jq over it for dropped_comments / token_usage / estimated_cost_usd) ==="
ls -t "src/ReviewBot.Api/traces/$OWNER/$REPO/${PR}-"*.json 2>/dev/null | head -1
