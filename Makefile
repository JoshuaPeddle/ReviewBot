.PHONY: eval-quick

eval-quick:
	mkdir -p runs
	dotnet run --project tests/ReviewBot.Evals -- score --fixtures tests/ReviewBot.Evals/Fixtures --results tests/ReviewBot.Evals/CannedResults/quick --out runs/eval-quick.json
