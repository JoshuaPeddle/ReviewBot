using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ReviewBot.E2E.Tests.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ReviewBot.E2E.Tests.Scenarios;

[Collection(E2eCollection.Name)]
public sealed class BasicReviewTests(ReviewBotHarness harness)
{
    private const string Owner = "owner";
    private const string Repo = "repo";
    private const int PullNumber = 1;
    private const long InstallationId = 98765;
    private const string HeadSha = "abc123";

    [Fact]
    public async Task ReviewRequestedPostsReviewWithTwoComments()
    {
        await harness.ResetAsync();
        ConfigureSuccessfulReview(
            repoConfig: FixtureLoader.ReadText("repo-config-openai.yml"),
            prFiles: FixtureLoader.ReadText("pr-files-dotnet.json"),
            llmReviewJson: FixtureLoader.ReadText("llm-response-two-comments.json"));
        using var client = harness.CreateClient();
        var sender = new WebhookSender(client, ReviewBotHarness.WebhookSecret);

        using var response = await sender.SendPullRequestAsync(
            FixtureLoader.ReadText("webhook-pr-review-requested.json"),
            deliveryId: "delivery-e2e-posts-two-comments");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WorkerSyncHelper.WaitForRequestAsync(
            harness.GitHubMock,
            IsReviewPostPath);

        using var reviewPayload = GetSingleRequestJson(harness.GitHubMock, "POST", IsReviewPostPath);
        reviewPayload.RootElement.GetProperty("commit_id").GetString().Should().Be(HeadSha);
        reviewPayload.RootElement.GetProperty("comments").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task ReviewRequestedWithSelfCritiquePostsHighConfidenceAndRetainedMediumComment()
    {
        await harness.ResetAsync();
        ConfigureSuccessfulSelfCritiqueReview(
            repoConfig: FixtureLoader.ReadText("repo-config-self-critique.yml"),
            prFiles: FixtureLoader.ReadText("pr-files-dotnet.json"),
            primaryReviewJson: FixtureLoader.ReadText("llm-response-self-critique-primary.json"),
            critiqueJson: """{"retained_indices":[0],"rationale":"second medium comment is style-only"}""");
        using var client = harness.CreateClient();
        var sender = new WebhookSender(client, ReviewBotHarness.WebhookSecret);

        using var response = await sender.SendPullRequestAsync(
            FixtureLoader.ReadText("webhook-pr-review-requested.json"),
            deliveryId: "delivery-e2e-self-critique");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WorkerSyncHelper.WaitForRequestAsync(
            harness.GitHubMock,
            IsReviewPostPath);

        WorkerSyncHelper.CountMatchingRequests(harness.LlmMock, "POST", IsOpenAiChatPath).Should().Be(2);
        var critiqueRequestWasSent = harness.LlmMock.LogEntries
            .Where(entry => entry.RequestMessage is { Path: "/v1/chat/completions" })
            .Select(entry => entry.RequestMessage!.Body)
            .Any(body => body != null && body.Contains("retained_indices", StringComparison.Ordinal));
        critiqueRequestWasSent.Should().BeTrue();

        using var reviewPayload = GetSingleRequestJson(harness.GitHubMock, "POST", IsReviewPostPath);
        var comments = reviewPayload.RootElement.GetProperty("comments").EnumerateArray().ToArray();
        comments.Should().HaveCount(2);
        comments.Select(comment => comment.GetProperty("body").GetString())
            .Should().Equal(
                "Console output in service code can leak noisy operational data.",
                "Returning an empty string may be surprising, depending on caller expectations.");
    }

    [Fact]
    public async Task ReviewRequestedWithAgenticContextFetchesRequestedFileAndPostsSecondPassComments()
    {
        await harness.ResetAsync();
        ConfigureSuccessfulAgenticContextReview(
            repoConfig: FixtureLoader.ReadText("repo-config-agentic-context.yml"),
            prFiles: FixtureLoader.ReadText("pr-files-dotnet.json"),
            primaryReviewJson: FixtureLoader.ReadText("llm-response-agentic-context-primary.json"),
            finalReviewJson: FixtureLoader.ReadText("llm-response-agentic-context-final.json"));
        using var client = harness.CreateClient();
        var sender = new WebhookSender(client, ReviewBotHarness.WebhookSecret);

        using var response = await sender.SendPullRequestAsync(
            FixtureLoader.ReadText("webhook-pr-review-requested.json"),
            deliveryId: "delivery-e2e-agentic-context");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WorkerSyncHelper.WaitForRequestAsync(
            harness.GitHubMock,
            IsReviewPostPath);

        WorkerSyncHelper.CountMatchingRequests(harness.LlmMock, "POST", IsOpenAiChatPath).Should().Be(2);
        var secondPassRequest = harness.LlmMock.LogEntries
            .Where(entry => entry.RequestMessage is { Path: "/v1/chat/completions" })
            .Select(entry => entry.RequestMessage!.Body)
            .Single(body => body != null && body.Contains("Additional context", StringComparison.Ordinal));
        secondPassRequest.Should().Contain("interface IUserRepository");
        secondPassRequest.Should().Contain("Returns null when the user is missing.");

        using var reviewPayload = GetSingleRequestJson(harness.GitHubMock, "POST", IsReviewPostPath);
        var comments = reviewPayload.RootElement.GetProperty("comments").EnumerateArray().ToArray();
        comments.Should().ContainSingle()
            .Which.GetProperty("body").GetString()
            .Should().Be("The service now returns an empty display name even though IUserRepository documents missing users as an error case.");
    }

    [Fact]
    public async Task ReviewRequestedIsIdempotentForDuplicateDelivery()
    {
        await harness.ResetAsync();
        ConfigureSuccessfulReview(
            repoConfig: FixtureLoader.ReadText("repo-config-openai.yml"),
            prFiles: FixtureLoader.ReadText("pr-files-dotnet.json"),
            llmReviewJson: FixtureLoader.ReadText("llm-response-two-comments.json"));
        using var client = harness.CreateClient();
        var sender = new WebhookSender(client, ReviewBotHarness.WebhookSecret);
        var payload = FixtureLoader.ReadText("webhook-pr-review-requested.json");

        using var firstResponse = await sender.SendPullRequestAsync(payload, deliveryId: "delivery-e2e-duplicate");
        firstResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WorkerSyncHelper.WaitForRequestCountAsync(harness.GitHubMock, IsReviewPostPath, expectedCount: 1);

        using var secondResponse = await sender.SendPullRequestAsync(payload, deliveryId: "delivery-e2e-duplicate");
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        await Task.Delay(TimeSpan.FromMilliseconds(250));

        WorkerSyncHelper.CountMatchingRequests(harness.LlmMock, "POST", IsOpenAiChatPath).Should().Be(1);
        WorkerSyncHelper.CountMatchingRequests(harness.GitHubMock, "POST", IsReviewPostPath).Should().Be(1);
    }

    [Fact]
    public async Task ReviewRequestedWithEmptyDiffSkipsLlmAndReviewPost()
    {
        await harness.ResetAsync();
        ConfigureSuccessfulReview(
            repoConfig: FixtureLoader.ReadText("repo-config-openai.yml"),
            prFiles: FixtureLoader.ReadText("pr-files-empty.json"),
            llmReviewJson: FixtureLoader.ReadText("llm-response-two-comments.json"));
        using var client = harness.CreateClient();
        var sender = new WebhookSender(client, ReviewBotHarness.WebhookSecret);

        using var response = await sender.SendPullRequestAsync(
            FixtureLoader.ReadText("webhook-pr-review-requested.json"),
            deliveryId: "delivery-e2e-empty-diff");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WorkerSyncHelper.WaitForNoCallAsync(harness.LlmMock, IsOpenAiChatPath);
        await WorkerSyncHelper.WaitForNoCallAsync(harness.GitHubMock, IsReviewPostPath);
    }

    [Fact]
    public async Task ReviewRequestedWithAllFilesIgnoredSkipsLlmAndReviewPost()
    {
        await harness.ResetAsync();
        ConfigureSuccessfulReview(
            repoConfig: FixtureLoader.ReadText("repo-config-ignore-all.yml"),
            prFiles: FixtureLoader.ReadText("pr-files-dotnet.json"),
            llmReviewJson: FixtureLoader.ReadText("llm-response-two-comments.json"));
        using var client = harness.CreateClient();
        var sender = new WebhookSender(client, ReviewBotHarness.WebhookSecret);

        using var response = await sender.SendPullRequestAsync(
            FixtureLoader.ReadText("webhook-pr-review-requested.json"),
            deliveryId: "delivery-e2e-all-ignored");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WorkerSyncHelper.WaitForNoCallAsync(harness.LlmMock, IsOpenAiChatPath);
        await WorkerSyncHelper.WaitForNoCallAsync(harness.GitHubMock, IsReviewPostPath);
    }

    private void ConfigureSuccessfulReview(
        string repoConfig,
        string prFiles,
        string llmReviewJson,
        string headSha = HeadSha)
    {
        StubInstallationToken();
        StubRepoConfig(repoConfig, headSha);
        StubPullRequest(headSha);
        StubPullRequestFiles(prFiles);
        StubDotNetGrounding(headSha);
        StubOpenAiReview(llmReviewJson);
        StubReviewPost();
    }

    private void ConfigureSuccessfulSelfCritiqueReview(
        string repoConfig,
        string prFiles,
        string primaryReviewJson,
        string critiqueJson,
        string headSha = HeadSha)
    {
        StubInstallationToken();
        StubRepoConfig(repoConfig, headSha);
        StubPullRequest(headSha);
        StubPullRequestFiles(prFiles);
        StubDotNetGrounding(headSha);
        StubOpenAiReviewWithSelfCritique(primaryReviewJson, critiqueJson);
        StubReviewPost();
    }

    private void ConfigureSuccessfulAgenticContextReview(
        string repoConfig,
        string prFiles,
        string primaryReviewJson,
        string finalReviewJson,
        string headSha = HeadSha)
    {
        StubInstallationToken();
        StubRepoConfig(repoConfig, headSha);
        StubPullRequest(headSha);
        StubPullRequestFiles(prFiles);
        StubDotNetGrounding(headSha);
        StubContextFile(
            "src/Contracts/IUserRepository.cs",
            """
            public interface IUserRepository
            {
                // Returns null when the user is missing.
                User? FindById(string id);
            }
            """);
        StubOpenAiReviewWithAgenticContext(primaryReviewJson, finalReviewJson);
        StubReviewPost();
    }

    private void StubInstallationToken()
    {
        harness.GitHubMock
            .Given(Request.Create()
                .WithPath($"/app/installations/{InstallationId}/access_tokens")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(201)
                .WithHeader("Content-Type", "application/json")
                .WithBody(FixtureLoader.ReadText("installation-token-response.json")));
    }

    private void StubRepoConfig(string yaml, string headSha)
    {
        harness.GitHubMock
            .Given(Request.Create()
                .WithPath("/repos/owner/repo/contents/.github/review-bot.yml")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(RepositoryContentResponse(".github/review-bot.yml", yaml)));
    }

    private void StubPullRequest(string headSha)
    {
        harness.GitHubMock
            .Given(Request.Create()
                .WithPath($"/repos/{Owner}/{Repo}/pulls/{PullNumber}")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                {
                  "url": "https://api.github.com/repos/{{Owner}}/{{Repo}}/pulls/{{PullNumber}}",
                  "html_url": "https://github.com/{{Owner}}/{{Repo}}/pull/{{PullNumber}}",
                  "diff_url": "https://github.com/{{Owner}}/{{Repo}}/pull/{{PullNumber}}.diff",
                  "patch_url": "https://github.com/{{Owner}}/{{Repo}}/pull/{{PullNumber}}.patch",
                  "issue_url": "https://api.github.com/repos/{{Owner}}/{{Repo}}/issues/{{PullNumber}}",
                  "statuses_url": "https://api.github.com/repos/{{Owner}}/{{Repo}}/statuses/{{headSha}}",
                  "number": {{PullNumber}},
                  "state": "open",
                  "title": "Improve user service",
                  "body": "Adds user-service handling.",
                  "created_at": "2026-05-23T12:00:00Z",
                  "updated_at": "2026-05-23T12:01:00Z",
                  "head": {
                    "label": "owner:feature",
                    "ref": "feature",
                    "sha": "{{headSha}}",
                    "repo": null,
                    "user": null
                  },
                  "base": {
                    "label": "owner:main",
                    "ref": "main",
                    "sha": "base123",
                    "repo": null,
                    "user": null
                  },
                  "user": {
                    "login": "developer"
                  }
                }
                """));
    }

    private void StubPullRequestFiles(string filesJson)
    {
        harness.GitHubMock
            .Given(Request.Create()
                .WithPath($"/repos/{Owner}/{Repo}/pulls/{PullNumber}/files")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(filesJson));
    }

    private void StubDotNetGrounding(string headSha)
    {
        harness.GitHubMock
            .Given(Request.Create()
                .WithPath($"/repos/{Owner}/{Repo}/git/trees/{headSha}")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                {
                  "sha": "abc123",
                  "url": "https://api.github.com/repos/owner/repo/git/trees/abc123",
                  "tree": [
                    {
                      "path": "Directory.Build.props",
                      "mode": "100644",
                      "type": "blob",
                      "sha": "props-sha",
                      "size": 128,
                      "url": "https://api.github.com/repos/owner/repo/git/blobs/props-sha"
                    }
                  ],
                  "truncated": false
                }
                """));

        harness.GitHubMock
            .Given(Request.Create()
                .WithPath($"/repos/{Owner}/{Repo}/contents/Directory.Build.props")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(RepositoryContentResponse(
                    "Directory.Build.props",
                    FixtureLoader.ReadText("directory-build-props.xml"))));

        harness.GitHubMock
            .Given(Request.Create()
                .WithPath($"/repos/{Owner}/{Repo}/contents/global.json")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(404)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"message":"Not Found"}"""));
    }

    private void StubContextFile(string path, string content)
    {
        harness.GitHubMock
            .Given(Request.Create()
                .WithPath($"/repos/{Owner}/{Repo}/contents/{path}")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(RepositoryContentResponse(path, content)));
    }

    private void StubOpenAiReview(string reviewJson)
    {
        harness.LlmMock
            .Given(Request.Create()
                .WithPath("/v1/chat/completions")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(OpenAiChatResponse(reviewJson)));
    }

    private void StubOpenAiReviewWithSelfCritique(string primaryReviewJson, string critiqueJson)
    {
        harness.LlmMock
            .Given(Request.Create()
                .WithPath("/v1/chat/completions")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(request =>
                {
                    var body = request.Body ?? string.Empty;
                    var response = body.Contains("retained_indices", StringComparison.Ordinal)
                        ? critiqueJson
                        : primaryReviewJson;
                    return OpenAiChatResponse(response);
                }));
    }

    private void StubOpenAiReviewWithAgenticContext(string primaryReviewJson, string finalReviewJson)
    {
        harness.LlmMock
            .Given(Request.Create()
                .WithPath("/v1/chat/completions")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(request =>
                {
                    var body = request.Body ?? string.Empty;
                    var response = body.Contains("Additional context", StringComparison.Ordinal)
                        ? finalReviewJson
                        : primaryReviewJson;
                    return OpenAiChatResponse(response);
                }));
    }

    private void StubReviewPost()
    {
        harness.GitHubMock
            .Given(Request.Create()
                .WithPath($"/repos/{Owner}/{Repo}/pulls/{PullNumber}/reviews")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"id": 12345}"""));
    }

    private static string RepositoryContentResponse(string path, string content)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        var name = Path.GetFileName(path);
        return $$"""
        [
          {
            "name": "{{name}}",
            "path": "{{path}}",
            "sha": "content-sha",
            "size": {{content.Length}},
            "type": "file",
            "download_url": "https://raw.githubusercontent.com/owner/repo/abc123/{{path}}",
            "url": "https://api.github.com/repos/owner/repo/contents/{{path}}",
            "git_url": "https://api.github.com/repos/owner/repo/git/blobs/content-sha",
            "html_url": "https://github.com/owner/repo/blob/abc123/{{path}}",
            "encoding": "base64",
            "content": "{{encoded}}"
          }
        ]
        """;
    }

    private static string OpenAiChatResponse(string content) =>
        $$"""
        {
          "id": "chatcmpl-e2e",
          "object": "chat.completion",
          "created": 1779537600,
          "model": "e2e-openai-model",
          "choices": [
            {
              "index": 0,
              "message": {
                "role": "assistant",
                "content": {{JsonSerializer.Serialize(content)}}
              },
              "finish_reason": "stop"
            }
          ],
          "usage": {
            "prompt_tokens": 100,
            "completion_tokens": 50,
            "total_tokens": 150
          }
        }
        """;

    private static JsonDocument GetSingleRequestJson(
        WireMockServer server,
        string method,
        Func<string, bool> pathPredicate)
    {
        var body = server.LogEntries
            .Where(entry =>
                entry.RequestMessage is { Path: not null } request &&
                string.Equals(request.Method, method, StringComparison.OrdinalIgnoreCase) &&
                pathPredicate(request.Path))
            .Select(entry => entry.RequestMessage!.Body)
            .Single();

        return JsonDocument.Parse(body!);
    }

    private static bool IsReviewPostPath(string path) =>
        path == $"/repos/{Owner}/{Repo}/pulls/{PullNumber}/reviews";

    private static bool IsOpenAiChatPath(string path) => path == "/v1/chat/completions";
}
