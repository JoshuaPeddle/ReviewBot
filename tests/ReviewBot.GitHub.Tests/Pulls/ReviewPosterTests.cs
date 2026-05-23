using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Octokit;
using ReviewBot.Core.Domain;
using ReviewBot.GitHub.Pulls;

namespace ReviewBot.GitHub.Tests.Pulls;

public class ReviewPosterTests
{
    [Fact]
    public async Task PostAsyncDropsCommentsOutsideCommentableLines()
    {
        var connection = CreateSuccessfulConnection();
        var poster = CreatePoster(connection);
        var result = new ReviewResult(
            "Looks good overall.",
            [
                new InlineComment("src/a.cs", 10, "RIGHT", "Valid", Severity.Warning),
                new InlineComment("src/a.cs", 12, "RIGHT", "Invalid", Severity.Warning),
            ]);

        await poster.PostAsync(
            "octo",
            "repo",
            42,
            "head-sha",
            result,
            [CreateFile("src/a.cs", 10)],
            "ghs_token",
            CancellationToken.None);

        var payload = await CapturePayloadAsync(connection);
        var comments = GetComments(payload);
        comments.Should().ContainSingle();
        comments[0]["line"].Should().Be(10);
        comments[0]["body"].Should().Be("Valid");
    }

    [Fact]
    public async Task PostAsyncDropsCommentsReferencingUnknownPaths()
    {
        var connection = CreateSuccessfulConnection();
        var poster = CreatePoster(connection);
        var result = new ReviewResult(
            "Looks good overall.",
            [
                new InlineComment("src/a.cs", 10, "RIGHT", "Valid", Severity.Warning),
                new InlineComment("src/missing.cs", 1, "RIGHT", "Invalid", Severity.Warning),
            ]);

        await poster.PostAsync(
            "octo",
            "repo",
            42,
            "head-sha",
            result,
            [CreateFile("src/a.cs", 10)],
            "ghs_token",
            CancellationToken.None);

        var payload = await CapturePayloadAsync(connection);
        var comments = GetComments(payload);
        comments.Should().ContainSingle();
        comments[0]["path"].Should().Be("src/a.cs");
    }

    [Fact]
    public async Task PostAsyncDoesNotCallApiWhenSummaryAndValidCommentsAreEmpty()
    {
        var connection = CreateSuccessfulConnection();
        var poster = CreatePoster(connection);
        var result = new ReviewResult(
            "   ",
            [new InlineComment("src/a.cs", 12, "RIGHT", "Invalid", Severity.Warning)]);

        await poster.PostAsync(
            "octo",
            "repo",
            42,
            "head-sha",
            result,
            [CreateFile("src/a.cs", 10)],
            "ghs_token",
            CancellationToken.None);

        await connection.DidNotReceive().Post(
            Arg.Any<Uri>(),
            Arg.Any<object>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostAsyncCallsApiWithReviewPayload()
    {
        var connection = CreateSuccessfulConnection();
        var poster = CreatePoster(connection);
        var result = new ReviewResult(
            "Summary body",
            [new InlineComment("src/a.cs", 10, "RIGHT", "Use a guard clause.", Severity.Warning)]);

        await poster.PostAsync(
            "octo",
            "repo",
            42,
            "head-sha",
            result,
            [CreateFile("src/a.cs", 10)],
            "ghs_token",
            CancellationToken.None);

        var payload = await CapturePayloadAsync(connection);
        payload["commit_id"].Should().Be("head-sha");
        payload["body"].Should().Be("Summary body");
        payload["event"].Should().Be("COMMENT");

        var comments = GetComments(payload);
        comments.Should().ContainSingle();
        comments[0].Should().Contain(new KeyValuePair<string, object>("path", "src/a.cs"));
        comments[0].Should().Contain(new KeyValuePair<string, object>("line", 10));
        comments[0].Should().Contain(new KeyValuePair<string, object>("side", "RIGHT"));
        comments[0].Should().Contain(new KeyValuePair<string, object>("body", "Use a guard clause."));

        await connection.Received(1).Post(
            Arg.Is<Uri>(uri => uri.ToString() == "repos/octo/repo/pulls/42/reviews"),
            Arg.Any<object>(),
            "application/vnd.github+json",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostAsyncUsesDefaultBodyWhenSummaryIsEmptyButCommentsAreValid()
    {
        var connection = CreateSuccessfulConnection();
        var poster = CreatePoster(connection);
        var result = new ReviewResult(
            string.Empty,
            [new InlineComment("src/a.cs", 10, "RIGHT", "Use a guard clause.", Severity.Warning)]);

        await poster.PostAsync(
            "octo",
            "repo",
            42,
            "head-sha",
            result,
            [CreateFile("src/a.cs", 10)],
            "ghs_token",
            CancellationToken.None);

        var payload = await CapturePayloadAsync(connection);
        payload["body"].Should().Be("Automated review by ReviewBot.");
    }

    [Fact]
    public async Task PostAsyncRethrowsValidationFailureAsReviewPostException()
    {
        var connection = Substitute.For<IConnection>();
        connection
            .Post(Arg.Any<Uri>(), Arg.Any<object>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<HttpStatusCode>(new ApiValidationException()));
        var poster = CreatePoster(connection);
        var result = new ReviewResult(
            "Summary body",
            [
                new InlineComment("src/a.cs", 10, "RIGHT", "Valid", Severity.Warning),
                new InlineComment("src/a.cs", 12, "RIGHT", "Invalid", Severity.Warning),
            ]);

        var act = () => poster.PostAsync(
            "octo",
            "repo",
            42,
            "head-sha",
            result,
            [CreateFile("src/a.cs", 10)],
            "ghs_token",
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ReviewPostException>();
        exception.Which.AcceptedCommentCount.Should().Be(1);
        exception.Which.DroppedCommentCount.Should().Be(1);
        exception.Which.InnerException.Should().BeOfType<ApiValidationException>();
    }

    private static ReviewPoster CreatePoster(IConnection connection)
    {
        var gitHubClient = Substitute.For<IGitHubClient>();
        gitHubClient.Connection.Returns(connection);

        var clientFactory = Substitute.For<IGitHubClientFactory>();
        clientFactory.CreateForInstallation("ghs_token").Returns(gitHubClient);

        return new ReviewPoster(clientFactory, NullLogger<ReviewPoster>.Instance);
    }

    private static IConnection CreateSuccessfulConnection()
    {
        var connection = Substitute.For<IConnection>();
        connection
            .Post(Arg.Any<Uri>(), Arg.Any<object>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(HttpStatusCode.Created);

        return connection;
    }

    private static async Task<Dictionary<string, object>> CapturePayloadAsync(IConnection connection)
    {
        await connection.Received(1).Post(
            Arg.Any<Uri>(),
            Arg.Is<object>(payload => payload is Dictionary<string, object>),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

        var call = connection
            .ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IConnection.Post));

        return call.GetArguments()[1].Should().BeOfType<Dictionary<string, object>>().Subject;
    }

    private static IReadOnlyList<Dictionary<string, object>> GetComments(Dictionary<string, object> payload) =>
        payload["comments"].Should().BeAssignableTo<IReadOnlyList<Dictionary<string, object>>>().Subject;

    private static FileChange CreateFile(string path, params int[] commentableLines) =>
        new(path, string.Empty, new HashSet<int>(commentableLines), 1, 0, FileChangeStatus.Modified);
}
