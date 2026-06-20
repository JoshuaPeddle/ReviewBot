#!/usr/bin/env bash
# Start ReviewBot locally with the demo GitHub App + configured LLM.
# Reads secrets/config from .env.local (gitignored). Run it in the background:
#
#   scripts/reviewbot-serve.sh > /tmp/reviewbot.log 2>&1 &
#   curl -fsS "$REVIEWBOT_LOCAL_URL/healthz"   # wait for 200, then trigger a review
#
set -euo pipefail
cd "$(dirname "$0")/.."

if [[ ! -f .env.local ]]; then
  echo "missing .env.local (see scripts/reviewbot-serve.sh header)" >&2
  exit 1
fi
set -a; . ./.env.local; set +a

export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="${REVIEWBOT_LOCAL_URL:-http://127.0.0.1:5174}"

exec dotnet run --project src/ReviewBot.Api
