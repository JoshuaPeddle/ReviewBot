using ReviewBot.Core.Domain;

namespace ReviewBot.Grounding.Build;

public interface ITestRunner
{
    string LanguageId { get; }
    Task<TestResult> RunAsync(string workspacePath, GroundingConfig config, CancellationToken ct);
}
