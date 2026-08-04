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
# Only the handful of knobs a caller legitimately aims at are overridable. Secrets are
# deliberately excluded: a stale exported token silently masking a rotated one in the file is
# the same class of bug pointing the other way.

reviewbot_load_env() {
  local overridable=(REVIEWBOT_LOCAL_URL REVIEWBOT_OWNER REVIEWBOT_REPO)
  local name saved=()

  for name in "${overridable[@]}"; do
    saved+=("$name=${!name-}")
  done

  if [[ ! -f .env.local ]]; then
    echo "missing .env.local (see scripts/reviewbot-serve.sh header)" >&2
    return 1
  fi

  set -a
  # shellcheck disable=SC1091
  . ./.env.local
  set +a

  local entry value
  for entry in "${saved[@]}"; do
    name="${entry%%=*}"
    value="${entry#*=}"
    # An empty override is treated as "not set" so `FOO= script.sh` doesn't blank the file's
    # value — the caller almost certainly meant to pass something and got it wrong.
    if [[ -n "$value" ]]; then
      export "$name=$value"
    fi
  done
}
