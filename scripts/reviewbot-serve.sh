#!/usr/bin/env bash
# Start ReviewBot locally with the demo GitHub App + configured LLM.
# Reads secrets/config from .env.local (gitignored). Run it in the background:
#
#   scripts/reviewbot-serve.sh > /tmp/reviewbot.log 2>&1 &
#   curl -fsS "$REVIEWBOT_LOCAL_URL/healthz"   # wait for 200, then trigger a review
#
# REVIEWBOT_LOCAL_URL can be overridden from the environment to run a second instance
# alongside one that already owns 5174:
#
#   REVIEWBOT_LOCAL_URL=http://127.0.0.1:5175 scripts/reviewbot-serve.sh
set -euo pipefail
cd "$(dirname "$0")/.."
. scripts/load-env.sh
reviewbot_load_env

export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="${REVIEWBOT_LOCAL_URL:-http://127.0.0.1:5174}"

# --no-launch-profile: launchSettings.json pins applicationUrl to 5174 and takes precedence
# over ASPNETCORE_URLS, so without this the address above is silently ignored and the server
# binds 5174 whatever you asked for. The profile only sets applicationUrl and
# ASPNETCORE_ENVIRONMENT, both of which are set here, so nothing is lost by skipping it.
exec dotnet run --project src/ReviewBot.Api --no-launch-profile --urls "$ASPNETCORE_URLS"
