namespace ReviewBot.Evals;

/// <summary>
/// Recognises a fixture result the runner wrote because the provider never answered.
/// </summary>
/// <remarks>
/// <see cref="LiveEvalRunner"/> records a placeholder result when a request times out
/// or errors, so downstream stages have a file to read. Those placeholders must not be
/// scored: the model expressed no opinion, so counting its absent findings as misses
/// turns an infrastructure failure into a recall failure. Measured on an n=3 baseline,
/// every false negative came from an aborted request rather than a missed bug, and the
/// abort rate (about 5% of fixture runs, from a reasoning model exhausting its output
/// allowance) was the largest single source of recall variance.
/// </remarks>
public static class EvalAbortDetector
{
    /// <summary>
    /// The prefix <see cref="LiveEvalRunner"/> writes into a placeholder result summary.
    /// </summary>
    public const string AbortedSummaryPrefix = "Eval fixture aborted:";

    public static bool IsAborted(string? rawResult) =>
        !string.IsNullOrWhiteSpace(rawResult) &&
        rawResult.Contains(AbortedSummaryPrefix, StringComparison.Ordinal);
}
