using ReviewBot.Core.Domain;

namespace ReviewBot.GitHub.Checks;

public interface ICheckRunFetcher
{
    Task<TestResult?> GetHeadCheckSummaryAsync(
        string owner,
        string repo,
        string headSha,
        string installationToken,
        CancellationToken ct);
}
