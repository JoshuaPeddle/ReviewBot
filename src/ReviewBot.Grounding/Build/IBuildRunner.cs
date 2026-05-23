using ReviewBot.Core.Domain;

namespace ReviewBot.Grounding.Build;

public interface IBuildRunner
{
    string LanguageId { get; }
    Task<BuildResult> RunAsync(string workspacePath, GroundingConfig config, CancellationToken ct);
}
