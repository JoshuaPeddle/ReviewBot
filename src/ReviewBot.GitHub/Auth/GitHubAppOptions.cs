namespace ReviewBot.GitHub.Auth;

public sealed record GitHubAppOptions
{
    public const string SectionName = "GitHubApp";
    private static readonly Uri DefaultApiBaseUrl = new("https://api.github.com/");

    public GitHubAppOptions()
    {
    }

    public GitHubAppOptions(long AppId, string PrivateKeyPem)
    {
        this.AppId = AppId;
        this.PrivateKeyPem = PrivateKeyPem;
    }

    public long AppId { get; set; }

    public string PrivateKeyPem { get; set; } = string.Empty;

    /// <summary>
    /// Path to a PEM file on disk. Used as a fallback when <see cref="PrivateKeyPem"/> is empty.
    /// Easier to configure in IDEs (e.g. Rider) where the env-var field is single-line.
    /// Set <c>GitHubApp:PrivateKeyPemFile</c> or <c>REVIEWBOT__GitHubApp__PrivateKeyPemFile</c>.
    /// </summary>
    public string PrivateKeyPemFile { get; set; } = string.Empty;

    public Uri ApiBaseUrl { get; set; } = DefaultApiBaseUrl;
}
