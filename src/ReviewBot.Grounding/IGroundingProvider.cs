using ReviewBot.Core.Domain;

namespace ReviewBot.Grounding;

public interface IGroundingProvider
{
    Task<GroundingContext> GetContextAsync(GroundingRequest request, CancellationToken ct);
}

public sealed record GroundingRequest(
    string Owner,
    string Repo,
    string HeadSha,
    string InstallationToken,
    GroundingConfig Config);
