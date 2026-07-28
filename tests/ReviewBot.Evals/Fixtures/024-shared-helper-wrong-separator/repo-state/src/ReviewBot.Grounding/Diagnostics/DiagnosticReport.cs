using ReviewBot.Core.Domain;

namespace ReviewBot.Grounding.Diagnostics;

/// <summary>
/// The outcome of running a diagnostic provider. <see cref="ToolRan"/> distinguishes
/// "the tool ran and found nothing wrong" from "the tool was unavailable" — the former
/// lets verification treat <see cref="AnalyzedPaths"/> as proven to parse cleanly (for
/// refutation), the latter must not.
/// </summary>
public sealed record DiagnosticReport(
    bool ToolRan,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<string> AnalyzedPaths)
{
    public static readonly DiagnosticReport NotRun =
        new(false, Array.Empty<Diagnostic>(), Array.Empty<string>());
}
