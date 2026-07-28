using Xunit;

// Several tests here attach a MeterListener to the process-wide ReviewBotLlmMetrics meter
// and assert the exact set of measurements recorded during one call. xUnit runs test
// classes in parallel by default, so a provider test in another class emits on that same
// meter concurrently and pollutes the captured list — the cause of intermittent CI
// failures in CompleteRawAsyncRecordsTokenUsageWithPhase and
// ReviewAsyncRecordsParseFailureMetricWithRepairOutcome, which passed and failed on the
// same commit.
//
// Metrics are process-global and only this assembly emits on that meter (other test
// assemblies run in their own processes), so serialising this assembly closes the race.
// It costs little: 71 tests, ~150ms total.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
