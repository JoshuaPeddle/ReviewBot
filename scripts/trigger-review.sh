#!/usr/bin/env bash
# Force the locally-running ReviewBot to review a real PR by POSTing a
# locally-signed synthetic GitHub `pull_request` webhook to /webhook.
#
#   scripts/trigger-review.sh <pr-number>
#
# Prints the delivery id; the worker then fetches the PR, calls the LLM, and
# posts a review to the PR on GitHub. Re-running on the same head SHA re-reviews
# the whole PR (the worker only skips when a delta has zero changed files).
set -euo pipefail
cd "$(dirname "$0")/.."
set -a; . ./.env.local; set +a

PR="${1:?usage: trigger-review.sh <pr-number>}"
OWNER="${REVIEWBOT_OWNER:?}"; REPO="${REVIEWBOT_REPO:?}"
URL="${REVIEWBOT_LOCAL_URL:-http://127.0.0.1:5174}"

SHA="$(gh api "repos/$OWNER/$REPO/pulls/$PR" --jq .head.sha)"
DELIVERY="local-$(date +%s)-$RANDOM"
PAYLOAD="$(printf '{"action":"opened","installation":{"id":%s},"repository":{"name":"%s","owner":{"login":"%s"}},"pull_request":{"number":%s,"html_url":"https://github.com/%s/%s/pull/%s","head":{"sha":"%s"},"user":{"login":"%s"}}}' \
  "$REVIEWBOT_INSTALLATION_ID" "$REPO" "$OWNER" "$PR" "$OWNER" "$REPO" "$PR" "$SHA" "$OWNER")"
# openssl prints "<algo>(stdin)= <hex>"; strip everything up to the "= " so the
# extraction is robust to the algo-label differences between openssl versions.
SIG="sha256=$(printf '%s' "$PAYLOAD" | openssl dgst -sha256 -hmac "$REVIEWBOT__Webhook__Secret" | sed 's/^.*= //')"

curl -fsS -X POST "$URL/webhook" \
  -H "X-GitHub-Event: pull_request" \
  -H "X-GitHub-Delivery: $DELIVERY" \
  -H "X-Hub-Signature-256: $SIG" \
  -H "Content-Type: application/json" \
  --data "$PAYLOAD" -w $'\nHTTP %{http_code}\n'

echo "delivery_id=$DELIVERY  pr=$PR  sha=${SHA:0:12}"
