namespace ReviewBot.GitHub.Auth;

public interface IInstallationTokenProvider
{
    Task<InstallationToken> GetTokenAsync(long installationId, CancellationToken ct);
}
