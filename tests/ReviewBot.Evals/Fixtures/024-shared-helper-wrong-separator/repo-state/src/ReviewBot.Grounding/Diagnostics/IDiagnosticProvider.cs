namespace ReviewBot.Grounding.Diagnostics;

/// <summary>
/// Produces compiler/analyzer diagnostics for a checked-out workspace, scoped to
/// the files a PR changed. Unlike <see cref="Build.IBuildRunner"/> (which is part
/// of grounding and may run a full build), a diagnostic provider is meant to be
/// cheap — a linter or type-checker — so verification can corroborate findings
/// against ground truth without requiring build grounding. Implementations must
/// degrade gracefully (return empty) when their tool is unavailable.
/// </summary>
public interface IDiagnosticProvider
{
    string LanguageId { get; }

    Task<DiagnosticReport> GetDiagnosticsAsync(
        string workspacePath,
        IReadOnlyList<string> changedPaths,
        CancellationToken ct);
}
