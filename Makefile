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
# Seed. Left unset on purpose.
#
# A fixed seed does NOT pair an A/B here. Measured 2026-07-29 against the reference
# SGLang server: three identical requests at seed=42 returned three different
# completions, and five 27-fixture runs produced different output for every single
# fixture regardless of seed. Servers treat the seed as best-effort and continuous
# batching perturbs it anyway. Believing otherwise is what let five single-run A/Bs
# read as authoritative when one run is worth about +/-2 fixtures / +/-0.05 F1.
#
# Set it if your server honours it — `make eval-probe` now reports whether this one
# does. The defence that actually works is EVAL_REPEATS below.
REVIEWBOT_EVAL_SEED ?=

# How many times to run each arm. A single run cannot resolve anything smaller than
# the corpus's own run-to-run spread: 8 of 27 fixtures flip between identical runs.
# Each arm is run this many times and reported as mean + range.
EVAL_REPEATS ?= 3

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
	@echo "Checking whether this server honours --seed ..."
	@body='{"model":"$(REVIEWBOT_EVAL_MODEL_NAME)","messages":[{"role":"user","content":"Invent a two-sentence story about a lighthouse."}],"max_tokens":4000,"temperature":0.6,"seed":42}'; \
	a=$$(curl -sS --max-time 120 "$(REVIEWBOT_EVAL_BASE_URL)/chat/completions" -H 'Content-Type: application/json' -d "$$body" | shasum | cut -c1-10); \
	b=$$(curl -sS --max-time 120 "$(REVIEWBOT_EVAL_BASE_URL)/chat/completions" -H 'Content-Type: application/json' -d "$$body" | shasum | cut -c1-10); \
	if [ "$$a" = "$$b" ]; then \
		echo "  seed honoured: YES (identical requests matched). A/B arms are paired."; \
	else \
		echo "  seed honoured: NO  (identical requests diverged: $$a vs $$b)."; \
		echo "  Run each arm EVAL_REPEATS=$(EVAL_REPEATS) times and compare mean + range;"; \
		echo "  a single run cannot resolve a delta smaller than the corpus's own spread."; \
	fi

# Runs one arm EVAL_REPEATS times and aggregates the scores.
#   $(1) arm name ("baseline" / "retrieval")   $(2) --retrieval value
#
# Repeats are not optional rigour here. 8 of 27 fixtures flip between identical runs,
# so one run per arm cannot resolve a delta of ~2 fixtures — which is most of what a
# change to prompting or filtering moves. Per-run scores are kept alongside the
# aggregate so `compare --baseline ... --candidate ...` can take the whole set.
define run-eval-arm
	@set -e; scores=""; \
	for i in $$(seq 1 $(EVAL_REPEATS)); do \
		echo "=== $(1) arm: run $$i of $(EVAL_REPEATS) ==="; \
		results=runs/eval-$(EVAL_RUN_LABEL)-$(1)-run$$i-results; \
		score=runs/eval-$(EVAL_RUN_LABEL)-$(1)-run$$i.json; \
		mkdir -p $$results; \
		dotnet run --project tests/ReviewBot.Evals -- run-live \
			--fixtures $(REVIEWBOT_EVAL_FIXTURES) \
			--results $$results \
			--base-url $(REVIEWBOT_EVAL_BASE_URL) \
			--model $(REVIEWBOT_EVAL_MODEL_NAME) \
			--api-key-env REVIEWBOT_EVAL_OPENAI_API_KEY \
			--retrieval $(2) \
			--context-tokens $(REVIEWBOT_EVAL_CONTEXT_TOKENS) \
			--index-cache-dir $(REVIEWBOT_EVAL_INDEX_CACHE_DIR) \
			--per-fixture-timeout $(REVIEWBOT_EVAL_PER_FIXTURE_TIMEOUT) \
			--request-timeout $(REVIEWBOT_EVAL_REQUEST_TIMEOUT) \
			--max-tokens $(REVIEWBOT_EVAL_MAX_TOKENS) \
			$(EVAL_SAMPLING_ARGS) \
			--manifest runs/eval-$(EVAL_RUN_LABEL)-$(1)-run$$i-manifest.json; \
		dotnet run --project tests/ReviewBot.Evals -- score \
			--fixtures $(REVIEWBOT_EVAL_FIXTURES) --results $$results --out $$score || true; \
		test -f $$score || { echo "ERROR: scoring produced no output JSON for run $$i"; exit 1; }; \
		scores="$$scores $$score"; \
	done; \
	dotnet run --project tests/ReviewBot.Evals -- aggregate $$scores \
		--out runs/eval-$(EVAL_RUN_LABEL)-$(1).json; \
	echo "Wrote runs/eval-$(EVAL_RUN_LABEL)-$(1).json (aggregate of $(EVAL_REPEATS) run(s))"
endef

# Live eval: retrieval OFF.
eval-live-baseline: eval-probe
	$(call run-eval-arm,baseline,false)

# Live eval: retrieval ON.
eval-live-retrieval: eval-probe
	$(call run-eval-arm,retrieval,true)

# End-to-end: baseline + retrieval + comparison. The whole point of this exists
# so the question "did retrieval move the needle?" is one command.
# `-` on the compare line because compare exits 1 when regressions are found
# (that's signal, not a Makefile failure — we want the JSON either way).
# Compares every run of each arm, not one against one, so the headline delta is read
# next to the run-to-run spread that could have produced it.
eval-live-compare: eval-live-baseline eval-live-retrieval
	@set -e; args=""; \
	for i in $$(seq 1 $(EVAL_REPEATS)); do \
		args="$$args --baseline runs/eval-$(EVAL_RUN_LABEL)-baseline-run$$i.json"; \
		args="$$args --candidate runs/eval-$(EVAL_RUN_LABEL)-retrieval-run$$i.json"; \
	done; \
	dotnet run --project tests/ReviewBot.Evals -- compare $$args \
		--out runs/eval-$(EVAL_RUN_LABEL)-comparison.json || true
	@test -f runs/eval-$(EVAL_RUN_LABEL)-comparison.json || { echo "ERROR: compare produced no output JSON"; exit 1; }
	@echo "Wrote runs/eval-$(EVAL_RUN_LABEL)-comparison.json"
