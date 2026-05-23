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

    public Uri ApiBaseUrl { get; set; } = DefaultApiBaseUrl;
}
