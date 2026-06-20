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

command -v gh >/dev/null  || { echo "error: 'gh' CLI not found on PATH" >&2; exit 1; }
command -v python3 >/dev/null || { echo "error: 'python3' not found on PATH" >&2; exit 1; }

PR="${1:?usage: trigger-review.sh <pr-number>}"
export REVIEWBOT_OWNER="${REVIEWBOT_OWNER:?}"; export REVIEWBOT_REPO="${REVIEWBOT_REPO:?}"
URL="${REVIEWBOT_LOCAL_URL:-http://127.0.0.1:5174}"

export REVIEWBOT_PR="$PR"
export REVIEWBOT_SHA="$(gh api "repos/$REVIEWBOT_OWNER/$REVIEWBOT_REPO/pulls/$PR" --jq .head.sha)"
DELIVERY="local-$(date +%s)-$RANDOM"
PAYLOAD_FILE="$(mktemp -t reviewbot-webhook)"
trap 'rm -f "$PAYLOAD_FILE"' EXIT

# Build the payload (json.dumps escapes safely) and HMAC-sign it in one step.
# The secret is read from the environment inside Python, never passed on a
# command line, so it can't leak via `ps`/argv. The signature is computed over
# the exact bytes written to the payload file, which curl then posts verbatim.
SIG="sha256=$(REVIEWBOT_INSTALLATION_ID="$REVIEWBOT_INSTALLATION_ID" \
  REVIEWBOT_PAYLOAD_FILE="$PAYLOAD_FILE" python3 - <<'PY'
import hashlib, hmac, json, os

owner, repo = os.environ["REVIEWBOT_OWNER"], os.environ["REVIEWBOT_REPO"]
pr = int(os.environ["REVIEWBOT_PR"])
payload = {
    "action": "opened",
    "installation": {"id": int(os.environ["REVIEWBOT_INSTALLATION_ID"])},
    "repository": {"name": repo, "owner": {"login": owner}},
    "pull_request": {
        "number": pr,
        "html_url": f"https://github.com/{owner}/{repo}/pull/{pr}",
        "head": {"sha": os.environ["REVIEWBOT_SHA"]},
        "user": {"login": owner},
    },
}
body = json.dumps(payload, separators=(",", ":")).encode()
with open(os.environ["REVIEWBOT_PAYLOAD_FILE"], "wb") as f:
    f.write(body)
print(hmac.new(os.environ["REVIEWBOT__Webhook__Secret"].encode(), body, hashlib.sha256).hexdigest())
PY
)"

curl -fsS -X POST "$URL/webhook" \
  -H "X-GitHub-Event: pull_request" \
  -H "X-GitHub-Delivery: $DELIVERY" \
  -H "X-Hub-Signature-256: $SIG" \
  -H "Content-Type: application/json" \
  --data-binary "@$PAYLOAD_FILE" -w $'\nHTTP %{http_code}\n'

echo "delivery_id=$DELIVERY  pr=$PR  sha=${REVIEWBOT_SHA:0:12}"
