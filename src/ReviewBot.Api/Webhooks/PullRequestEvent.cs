using System.Text.Json.Serialization;

namespace ReviewBot.Api.Webhooks;

internal sealed record PullRequestEvent
{
    [JsonPropertyName("action")]
    public string Action { get; init; } = "";

    [JsonPropertyName("installation")]
    public InstallationRef Installation { get; init; } = new();

    [JsonPropertyName("repository")]
    public RepositoryRef Repository { get; init; } = new();

    [JsonPropertyName("pull_request")]
    public PullRequestRef PullRequest { get; init; } = new();

    [JsonPropertyName("requested_reviewer")]
    public UserRef? RequestedReviewer { get; init; }

    [JsonPropertyName("sender")]
    public UserRef? Sender { get; init; }
}

internal sealed record InstallationRef
{
    [JsonPropertyName("id")]
    public long Id { get; init; }
}

internal sealed record RepositoryRef
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("owner")]
    public UserRef Owner { get; init; } = new();
}

internal sealed record PullRequestRef
{
    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; init; } = "";

    [JsonPropertyName("head")]
    public PullRequestHeadRef Head { get; init; } = new();

    [JsonPropertyName("user")]
    public UserRef User { get; init; } = new();

    [JsonPropertyName("requested_reviewers")]
    public IReadOnlyList<UserRef> RequestedReviewers { get; init; } = [];
}

internal sealed record PullRequestHeadRef
{
    [JsonPropertyName("sha")]
    public string Sha { get; init; } = "";
}

internal sealed record UserRef
{
    [JsonPropertyName("login")]
    public string Login { get; init; } = "";
}
