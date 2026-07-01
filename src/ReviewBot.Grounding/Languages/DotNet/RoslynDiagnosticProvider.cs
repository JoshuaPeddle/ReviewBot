using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewBot.Grounding.Diagnostics;
using DomainDiagnostic = ReviewBot.Core.Domain.Diagnostic;
using DomainSeverity = ReviewBot.Core.Domain.DiagnosticSeverity;

namespace ReviewBot.Grounding.Languages.DotNet;

/// <summary>
/// Build-free C# diagnostics via the Roslyn parser. Parses each changed <c>.cs</c>
/// file into a syntax tree and reports the parse (syntactic) diagnostics, which are
/// reference-independent — so this needs no restore, no SDK, and no project load, and
/// is safe to leave enabled everywhere. A changed file that parses with no error is
/// listed in <see cref="DiagnosticReport.AnalyzedPaths"/> as proven to parse, which
/// lets verification refute hallucinated "invalid C# syntax / won't compile" claims
/// against it (the recurring false-positive class this project fights); emitted syntax
/// errors corroborate genuine ones.
///
/// Scope note: parsing proves a file <em>parses</em>, not that the whole project
/// <em>compiles</em> (type resolution needs references). Semantic analyzer diagnostics —
/// the security / performance / correctness moat — are a later, build-grounding-class
/// tier. This provider is the always-on syntax floor and shares the parse→refute
/// contract with <see cref="Python.RuffDiagnosticProvider"/>.
/// </summary>
public sealed class RoslynDiagnosticProvider : IDiagnosticProvider
{
    // Preview accepts the newest language syntax, so valid modern C# (collection
    // expressions, primary constructors, raw strings, …) never registers as a false
    // syntax error against an older language version.
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private readonly ILogger<RoslynDiagnosticProvider> logger;

    public RoslynDiagnosticProvider(ILogger<RoslynDiagnosticProvider>? logger = null)
    {
        this.logger = logger ?? NullLogger<RoslynDiagnosticProvider>.Instance;
    }

    public string LanguageId => "dotnet";

    public async Task<DiagnosticReport> GetDiagnosticsAsync(
        string workspacePath,
        IReadOnlyList<string> changedPaths,
        CancellationToken ct)
    {
        var csharpFiles = changedPaths
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(IsSafeRelativePath)
            .ToArray();
        if (csharpFiles.Length == 0)
        {
            return DiagnosticReport.NotRun;
        }

        var diagnostics = new List<DomainDiagnostic>();
        var analyzedPaths = new List<string>();
        foreach (var relativePath in csharpFiles)
        {
            ct.ThrowIfCancellationRequested();

            string text;
            try
            {
                // A changed path may be a deletion or otherwise absent from the head
                // checkout; skip it rather than treat it as analyzed (proven to parse).
                text = await File
                    .ReadAllTextAsync(Path.Combine(workspacePath, relativePath), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.logger.LogDebug(ex, "Roslyn diagnostics: could not read {Path}; skipping", relativePath);
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(text, ParseOptions, path: relativePath, cancellationToken: ct);
            // A parse-only tree yields only syntactic diagnostics — reference-independent
            // and therefore sound without a compilation.
            foreach (var diagnostic in tree.GetDiagnostics(ct))
            {
                if (Map(diagnostic, relativePath) is { } mapped)
                {
                    diagnostics.Add(mapped);
                }
            }

            analyzedPaths.Add(relativePath);
        }

        if (analyzedPaths.Count == 0)
        {
            // Every candidate file was unreadable — nothing was proven to parse, so the
            // report must not license refutation (ToolRan stays false).
            return DiagnosticReport.NotRun;
        }

        return new DiagnosticReport(true, diagnostics, analyzedPaths);
    }

    private static DomainDiagnostic? Map(Diagnostic diagnostic, string relativePath)
    {
        DomainSeverity? severity = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => DomainSeverity.Error,
            DiagnosticSeverity.Warning => DomainSeverity.Warning,
            _ => null // Info / Hidden carry no review signal.
        };
        if (severity is null)
        {
            return null;
        }

        var line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1;
        return new DomainDiagnostic(
            relativePath,
            line,
            severity.Value,
            diagnostic.Id,
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    // Defence in depth: changed paths come from Git, but they are combined into a
    // filesystem path here, so reject anything that could escape the workspace before
    // it is read.
    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.StartsWith('/') ||
            path.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        return path.Split('/').All(segment =>
            !string.IsNullOrEmpty(segment) && segment != "." && segment != "..");
    }
}
