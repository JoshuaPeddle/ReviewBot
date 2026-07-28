namespace ReviewBot.Core.Domain;

public sealed record GroundingContext(
    LanguageMetadata? Language,
    BuildResult? Build,
    TestResult? Tests);

public sealed record LanguageMetadata(
    string LanguageId,
    string LanguageVersion,
    string? ToolchainVersion,
    IReadOnlyList<string> Facts);

public sealed record BuildResult(
    bool Success,
    int Warnings,
    int Errors,
    string Output,
    IReadOnlyList<Diagnostic>? Diagnostics = null)
{
    // Per-file build diagnostics (file/line/code/message) parsed from build output.
    // Empty when the runner did not emit structured diagnostics, so consumers can
    // iterate without null checks.
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = Diagnostics ?? Array.Empty<Diagnostic>();
}

public sealed record TestResult(int Passed, int Failed, int Skipped, string Output, string Source = "local");

/// <summary>
/// A single compiler/analyzer diagnostic at a repo-relative path and 1-based line.
/// Used to corroborate review findings against ground truth.
/// </summary>
public sealed record Diagnostic(
    string Path,
    int Line,
    DiagnosticSeverity Severity,
    string Code,
    string Message);

public enum DiagnosticSeverity
{
    Warning = 0,
    Error = 1
}
