# Shared by the dogfood scripts: load .env.local, but let the caller's environment win.
#
# The scripts used to do `set -a; . ./.env.local; set +a` and *then* read
# REVIEWBOT_LOCAL_URL, so every value in the file was exported over anything the caller had
# already set. `REVIEWBOT_LOCAL_URL=http://127.0.0.1:5175 scripts/trigger-review.sh 63`
# therefore aimed at whatever the file said, silently, while still printing HTTP 202 — which
# is how a dogfood run ends up reviewed by a different instance than the one you started and
# nobody notices. Sourcing has to happen before the values are read (the file is where they
# come from), so the fix is to re-apply the caller's overrides afterwards.
#
# Every REVIEWBOT* variable already set in the environment is preserved, not a fixed list.
# The first version of this allowlisted three names on the theory that secrets should stay
# file-only, and that immediately caused the bug it was written to prevent: pointing a test
# instance at a different LLM endpoint with REVIEWBOT__OpenAi__BaseUrl was silently ignored,
# so three review runs went to a decommissioned endpoint and 404'd while the log insisted the
# override had been applied. An override that is quietly dropped is worse than one that
# shadows a rotated secret, because nothing tells you it happened.

reviewbot_load_env() {
  # `local name saved=()` then expanding "${saved[@]}" while empty is an unbound-variable
  # error under `set -u` on bash 3.2 (the macOS default), which is the *normal* path: a
  # caller that overrides nothing has nothing to snapshot. Every use during development set
  # REVIEWBOT_LOCAL_URL, so the plain `scripts/read-review.sh <pr>` invocation was the one
  # case never exercised — and the one that broke.
  local name
  local saved=()

  # Snapshot every REVIEWBOT* variable the caller set. Restricting to that prefix keeps the
  # restore away from PATH and friends, which the env file has no business setting anyway.
  while IFS= read -r name; do
    [[ -n "$name" ]] && saved+=("$name=${!name-}")
  done < <(compgen -v | grep '^REVIEWBOT' || true)

  if [[ ! -f .env.local ]]; then
    echo "missing .env.local (see scripts/reviewbot-serve.sh header)" >&2
    return 1
  fi

  set -a
  # shellcheck disable=SC1091
  . ./.env.local
  set +a

  local entry value
  for entry in ${saved[@]+"${saved[@]}"}; do
    name="${entry%%=*}"
    value="${entry#*=}"
    # An empty override is treated as "not set" so `FOO= script.sh` doesn't blank the file's
    # value — the caller almost certainly meant to pass something and got it wrong.
    if [[ -n "$value" ]]; then
      export "$name=$value"
    fi
  done
}
