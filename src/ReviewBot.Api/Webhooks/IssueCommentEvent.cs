using System.Text.Json.Serialization;

namespace ReviewBot.Api.Webhooks;

internal sealed record IssueCommentEvent
{
    [JsonPropertyName("action")]
    public string Action { get; init; } = "";

    [JsonPropertyName("installation")]
    public InstallationRef Installation { get; init; } = new();

    [JsonPropertyName("repository")]
    public RepositoryRef Repository { get; init; } = new();

    [JsonPropertyName("issue")]
    public IssueRef Issue { get; init; } = new();

    [JsonPropertyName("comment")]
    public CommentRef Comment { get; init; } = new();
}

internal sealed record IssueRef
{
    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("pull_request")]
    public PullRequestLinkRef? PullRequest { get; init; }
}

internal sealed record PullRequestLinkRef
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = "";
}

internal sealed record CommentRef
{
    [JsonPropertyName("body")]
    public string Body { get; init; } = "";

    [JsonPropertyName("user")]
    public UserRef User { get; init; } = new();
}
