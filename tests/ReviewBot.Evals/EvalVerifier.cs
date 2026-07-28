using ReviewBot.Core.Domain;
using ReviewBot.Core.Verification;
using ReviewBot.Grounding.Diagnostics;
using ReviewBot.Grounding.Languages.DotNet;
using ReviewBot.Grounding.Languages.Python;

namespace ReviewBot.Evals;

/// <summary>
/// Applies the worker's deterministic verification stage to an LLM result inside the
/// eval harness: runs the build-free diagnostic providers over the fixture's on-disk
/// <c>repo-state</c>, then refutes compile/syntax-failure claims on files proven to
/// parse cleanly and marks diagnostic-corroborated findings Verified — mirroring
/// <c>ReviewWorker.ApplyVerificationAsync</c>. Without this, the corpus scores raw model
/// output and can never measure a precision gain from verification (the Pillar-1 moat).
/// </summary>
public sealed class EvalVerifier
{
    private readonly IReadOnlyList<IDiagnosticProvider> providers;

    public EvalVerifier(IReadOnlyList<IDiagnosticProvider>? providers = null)
    {
        this.providers = providers ?? [new RoslynDiagnosticProvider(), new RuffDiagnosticProvider()];
    }

    public async Task<ReviewResult> VerifyAsync(
        EvalFixture fixture,
        ReviewResult result,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(result);

        if (result.Comments.Count == 0 || this.providers.Count == 0)
        {
            return result;
        }

        var repoState = Path.Combine(fixture.DirectoryPath, "repo-state");
        if (!Directory.Exists(repoState))
        {
            return result;
        }

        var changedPaths = EvalDiffParser.ParseFiles(fixture.DiffPatch)
            .Select(file => file.Path)
            .ToArray();
        if (changedPaths.Length == 0)
        {
            return result;
        }

        var diagnostics = new List<Diagnostic>();
        var cleanlyParsed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in this.providers)
        {
            var report = await provider.GetDiagnosticsAsync(repoState, changedPaths, ct).ConfigureAwait(false);
            diagnostics.AddRange(report.Diagnostics);
            if (!report.ToolRan)
            {
                continue;
            }

            var erroredPaths = report.Diagnostics
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.Path)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var path in report.AnalyzedPaths.Where(path => !erroredPaths.Contains(path)))
            {
                cleanlyParsed.Add(path);
            }
        }

        var refutation = FindingRefuter.Refute(result.Comments, cleanlyParsed);
        var corroborated = FindingCorroborator.Corroborate(refutation.Kept, diagnostics);
        return result with { Comments = corroborated.Select(finding => finding.Comment).ToArray() };
    }
}
