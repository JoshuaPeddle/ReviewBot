.PHONY: eval-quick eval-probe eval-live-baseline eval-live-retrieval eval-live-compare

# Load local eval config (LAN URL, model name, API key) if present.
-include .env.eval
export

# Defaults for things that are stable across machines. Anything that varies
# (URL, model name, API key) should live in .env.eval.
REVIEWBOT_EVAL_BASE_URL ?= http://localhost:11434/v1
REVIEWBOT_EVAL_MODEL_NAME ?= qwen/qwen3.6-27b
REVIEWBOT_EVAL_OPENAI_API_KEY ?= ollama
REVIEWBOT_EVAL_CONTEXT_TOKENS ?= 65536
REVIEWBOT_EVAL_FIXTURES ?= tests/ReviewBot.Evals/Fixtures
REVIEWBOT_EVAL_INDEX_CACHE_DIR ?= runs/eval-index
# Per-fixture wall-clock cap. If the LLM hangs on a single fixture (large
# prompt, stalled stream, server crash) we want to fail fast and move on
# rather than burn an hour silently. Default tuned for a 27B thinking model.
REVIEWBOT_EVAL_PER_FIXTURE_TIMEOUT ?= 240
REVIEWBOT_EVAL_REQUEST_TIMEOUT ?= 180
# Output token cap for each eval request. 4096 was sized for a 32K context model
# and is ample for the JSON response itself — but a reasoning model spends this
# budget thinking before it answers, and on the larger fixtures it runs out and
# returns nothing. Measured: an n=3 baseline aborted 4 of 78 fixture runs that way
# (024 three times, 026 once), and EVERY false negative in that baseline was an
# aborted request rather than a reasoning failure — silently depressing recall.
# 16384 matches the response reserve the worker derives for a 100K window.
REVIEWBOT_EVAL_MAX_TOKENS ?= 16384
# Sampling knobs. Empty means "don't send it" — the server's own default applies.
# Qwen3.6 recommends temperature=0.6, top_p=0.95, top_k=20 for thinking mode; the
# remaining three are identity values kept explicit so a run is fully reproducible.
REVIEWBOT_EVAL_TEMPERATURE ?=
REVIEWBOT_EVAL_TOP_P ?=
REVIEWBOT_EVAL_TOP_K ?=
REVIEWBOT_EVAL_MIN_P ?=
REVIEWBOT_EVAL_PRESENCE_PENALTY ?=
REVIEWBOT_EVAL_REPETITION_PENALTY ?=
# Fixing the seed pairs an A/B: both arms draw the same randomness, so a delta is
# attributable to the change under test rather than to sampling luck.
REVIEWBOT_EVAL_SEED ?=

# Expand to CLI flags only for the knobs that were actually set.
EVAL_SAMPLING_ARGS := \
	$(if $(REVIEWBOT_EVAL_TEMPERATURE),--temperature $(REVIEWBOT_EVAL_TEMPERATURE)) \
	$(if $(REVIEWBOT_EVAL_TOP_P),--top-p $(REVIEWBOT_EVAL_TOP_P)) \
	$(if $(REVIEWBOT_EVAL_TOP_K),--top-k $(REVIEWBOT_EVAL_TOP_K)) \
	$(if $(REVIEWBOT_EVAL_MIN_P),--min-p $(REVIEWBOT_EVAL_MIN_P)) \
	$(if $(REVIEWBOT_EVAL_PRESENCE_PENALTY),--presence-penalty $(REVIEWBOT_EVAL_PRESENCE_PENALTY)) \
	$(if $(REVIEWBOT_EVAL_REPETITION_PENALTY),--repetition-penalty $(REVIEWBOT_EVAL_REPETITION_PENALTY)) \
	$(if $(REVIEWBOT_EVAL_SEED),--seed $(REVIEWBOT_EVAL_SEED))

# Single timestamp per `make` invocation. Used so baseline + retrieval + comparison
# files from one run share a prefix.
EVAL_TIMESTAMP := $(shell date -u +%Y%m%d-%H%M%S)
EVAL_RUN_LABEL ?= $(EVAL_TIMESTAMP)

# Canned smoke against committed result files — no LLM call.
eval-quick:
	mkdir -p runs
	dotnet run --project tests/ReviewBot.Evals -- score \
		--fixtures tests/ReviewBot.Evals/Fixtures \
		--results tests/ReviewBot.Evals/CannedResults/quick \
		--out runs/eval-quick.json

# Fail fast if the configured OpenAI-compatible endpoint is not reachable.
# Useful before kicking off a long live eval that would otherwise crash
# halfway through. Hits /models because /v1/models is the canonical health
# probe on Ollama and most compatible servers.
eval-probe:
	@echo "Probing $(REVIEWBOT_EVAL_BASE_URL)/models ..."
	@status=$$(curl -sS --connect-timeout 3 --max-time 10 -o /dev/null -w "%{http_code}" "$(REVIEWBOT_EVAL_BASE_URL)/models" || true); \
	if [ "$$status" != "200" ]; then \
		echo "FAIL: $(REVIEWBOT_EVAL_BASE_URL)/models returned '$$status' (expected 200)."; \
		echo "Check that the LLM server is running and that this machine can reach it."; \
		exit 1; \
	fi
	@echo "OK: $(REVIEWBOT_EVAL_BASE_URL) reachable. Model target: $(REVIEWBOT_EVAL_MODEL_NAME)."

# Live eval: retrieval OFF. Writes per-fixture results + manifest, then scores
# them into a single aggregate JSON.
eval-live-baseline: eval-probe
	@mkdir -p runs/eval-$(EVAL_RUN_LABEL)-baseline-results
	dotnet run --project tests/ReviewBot.Evals -- run-live \
		--fixtures $(REVIEWBOT_EVAL_FIXTURES) \
		--results runs/eval-$(EVAL_RUN_LABEL)-baseline-results \
		--base-url $(REVIEWBOT_EVAL_BASE_URL) \
		--model $(REVIEWBOT_EVAL_MODEL_NAME) \
		--api-key-env REVIEWBOT_EVAL_OPENAI_API_KEY \
		--retrieval false \
		--context-tokens $(REVIEWBOT_EVAL_CONTEXT_TOKENS) \
		--index-cache-dir $(REVIEWBOT_EVAL_INDEX_CACHE_DIR) \
		--per-fixture-timeout $(REVIEWBOT_EVAL_PER_FIXTURE_TIMEOUT) \
		--request-timeout $(REVIEWBOT_EVAL_REQUEST_TIMEOUT) \
		--max-tokens $(REVIEWBOT_EVAL_MAX_TOKENS) \
		$(EVAL_SAMPLING_ARGS) \
		--manifest runs/eval-$(EVAL_RUN_LABEL)-baseline-manifest.json
	-dotnet run --project tests/ReviewBot.Evals -- score \
		--fixtures $(REVIEWBOT_EVAL_FIXTURES) \
		--results runs/eval-$(EVAL_RUN_LABEL)-baseline-results \
		--out runs/eval-$(EVAL_RUN_LABEL)-baseline.json
	@test -f runs/eval-$(EVAL_RUN_LABEL)-baseline.json || { echo "ERROR: scoring produced no output JSON"; exit 1; }
	@echo "Wrote runs/eval-$(EVAL_RUN_LABEL)-baseline.json"

# Live eval: retrieval ON. Same shape as baseline.
eval-live-retrieval: eval-probe
	@mkdir -p runs/eval-$(EVAL_RUN_LABEL)-retrieval-results
	dotnet run --project tests/ReviewBot.Evals -- run-live \
		--fixtures $(REVIEWBOT_EVAL_FIXTURES) \
		--results runs/eval-$(EVAL_RUN_LABEL)-retrieval-results \
		--base-url $(REVIEWBOT_EVAL_BASE_URL) \
		--model $(REVIEWBOT_EVAL_MODEL_NAME) \
		--api-key-env REVIEWBOT_EVAL_OPENAI_API_KEY \
		--retrieval true \
		--context-tokens $(REVIEWBOT_EVAL_CONTEXT_TOKENS) \
		--index-cache-dir $(REVIEWBOT_EVAL_INDEX_CACHE_DIR) \
		--per-fixture-timeout $(REVIEWBOT_EVAL_PER_FIXTURE_TIMEOUT) \
		--request-timeout $(REVIEWBOT_EVAL_REQUEST_TIMEOUT) \
		--max-tokens $(REVIEWBOT_EVAL_MAX_TOKENS) \
		$(EVAL_SAMPLING_ARGS) \
		--manifest runs/eval-$(EVAL_RUN_LABEL)-retrieval-manifest.json
	-dotnet run --project tests/ReviewBot.Evals -- score \
		--fixtures $(REVIEWBOT_EVAL_FIXTURES) \
		--results runs/eval-$(EVAL_RUN_LABEL)-retrieval-results \
		--out runs/eval-$(EVAL_RUN_LABEL)-retrieval.json
	@test -f runs/eval-$(EVAL_RUN_LABEL)-retrieval.json || { echo "ERROR: scoring produced no output JSON"; exit 1; }
	@echo "Wrote runs/eval-$(EVAL_RUN_LABEL)-retrieval.json"

# End-to-end: baseline + retrieval + comparison. The whole point of this exists
# so the question "did retrieval move the needle?" is one command.
# `-` on the compare line because compare exits 1 when regressions are found
# (that's signal, not a Makefile failure — we want the JSON either way).
eval-live-compare: eval-live-baseline eval-live-retrieval
	-dotnet run --project tests/ReviewBot.Evals -- compare \
		runs/eval-$(EVAL_RUN_LABEL)-baseline.json \
		runs/eval-$(EVAL_RUN_LABEL)-retrieval.json \
		--out runs/eval-$(EVAL_RUN_LABEL)-comparison.json
	@test -f runs/eval-$(EVAL_RUN_LABEL)-comparison.json || { echo "ERROR: compare produced no output JSON"; exit 1; }
	@echo "Wrote runs/eval-$(EVAL_RUN_LABEL)-comparison.json"
