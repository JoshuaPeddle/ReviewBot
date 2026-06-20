using ReviewBot.Core.Domain;
using ReviewBot.Grounding.Workspace;

namespace ReviewBot.Grounding;

public interface IGroundingProvider
{
    /// <param name="sharedWorkspace">
    /// Optional job-scoped workspace. When supplied, grounding clones through it
    /// so the same checkout is reused by retrieval indexing and finding
    /// verification and disposed once at job end; when null, grounding clones and
    /// disposes its own workspace (the standalone/unit-test path).
    /// </param>
    Task<GroundingContext> GetContextAsync(
        GroundingRequest request,
        CancellationToken ct,
        ISharedWorkspace? sharedWorkspace = null);
}

public sealed record GroundingRequest(
    string Owner,
    string Repo,
    string HeadSha,
    string InstallationToken,
    GroundingConfig Config,
    string? HeadCloneUrl = null);
