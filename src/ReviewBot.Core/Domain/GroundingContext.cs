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

public sealed record BuildResult(bool Success, int Warnings, int Errors, string Output);

public sealed record TestResult(int Passed, int Failed, int Skipped, string Output, string Source = "local");
