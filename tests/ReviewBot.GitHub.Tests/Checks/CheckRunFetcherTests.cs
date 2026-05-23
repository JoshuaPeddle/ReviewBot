using FluentAssertions;
using NSubstitute;
using Octokit;
using ReviewBot.GitHub.Checks;
using ReviewBot.GitHub.Pulls;

namespace ReviewBot.GitHub.Tests.Checks;

public class CheckRunFetcherTests
{
    [Fact]
    public async Task GetHeadCheckSummaryAsyncReturnsPassedResultWhenChecksSucceed()
    {
        var (clientFactory, checkRuns, statuses) = CreateClientFactory();
        checkRuns.GetAllForReference("octo", "repo", "head-sha")
            .Returns(CreateCheckRuns(CreateCheckRun("build", CheckConclusion.Success)));
        statuses.GetCombined("octo", "repo", "head-sha")
            .Returns(CreateCombinedStatus());
        var fetcher = new CheckRunFetcher(clientFactory);

        var result = await fetcher.GetHeadCheckSummaryAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Source.Should().Be("github_checks");
        result.Passed.Should().Be(1);
        result.Failed.Should().Be(0);
        result.Skipped.Should().Be(0);
        result.Output.Should().Contain("build: success");
    }

    [Fact]
    public async Task GetHeadCheckSummaryAsyncReturnsFailedResultWhenAnyCheckFails()
    {
        var (clientFactory, checkRuns, statuses) = CreateClientFactory();
        checkRuns.GetAllForReference("octo", "repo", "head-sha")
            .Returns(CreateCheckRuns(
                CreateCheckRun("unit tests", CheckConclusion.Success),
                CreateCheckRun("lint", CheckConclusion.Failure)));
        statuses.GetCombined("octo", "repo", "head-sha")
            .Returns(CreateCombinedStatus());
        var fetcher = new CheckRunFetcher(clientFactory);

        var result = await fetcher.GetHeadCheckSummaryAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Passed.Should().Be(1);
        result.Failed.Should().Be(1);
        result.Output.Should().Contain("lint: failure");
    }

    [Fact]
    public async Task GetHeadCheckSummaryAsyncIncludesCommitStatuses()
    {
        var (clientFactory, checkRuns, statuses) = CreateClientFactory();
        checkRuns.GetAllForReference("octo", "repo", "head-sha")
            .Returns(CreateCheckRuns());
        statuses.GetCombined("octo", "repo", "head-sha")
            .Returns(CreateCombinedStatus(
                CreateCommitStatus("ci/build", CommitState.Success, "ok"),
                CreateCommitStatus("ci/security", CommitState.Failure, "scan failed")));
        var fetcher = new CheckRunFetcher(clientFactory);

        var result = await fetcher.GetHeadCheckSummaryAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Passed.Should().Be(1);
        result.Failed.Should().Be(1);
        result.Output.Should().Contain("status ci/security: failure - scan failed");
    }

    [Fact]
    public async Task GetHeadCheckSummaryAsyncReturnsNullWhenNoCompletedChecksOrStatusesExist()
    {
        var (clientFactory, checkRuns, statuses) = CreateClientFactory();
        checkRuns.GetAllForReference("octo", "repo", "head-sha")
            .Returns(CreateCheckRuns(CreateCheckRun("queued", CheckConclusion.Success, CheckStatus.Queued)));
        statuses.GetCombined("octo", "repo", "head-sha")
            .Returns(CreateCombinedStatus(CreateCommitStatus("ci/pending", CommitState.Pending)));
        var fetcher = new CheckRunFetcher(clientFactory);

        var result = await fetcher.GetHeadCheckSummaryAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetHeadCheckSummaryAsyncPropagatesGitHubApiFailures()
    {
        var (clientFactory, checkRuns, _) = CreateClientFactory();
        checkRuns.GetAllForReference("octo", "repo", "head-sha")
            .Returns(_ => Task.FromException<CheckRunsResponse>(
                new ApiException("boom", System.Net.HttpStatusCode.InternalServerError)));
        var fetcher = new CheckRunFetcher(clientFactory);

        var act = () => fetcher.GetHeadCheckSummaryAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        await act.Should().ThrowAsync<ApiException>();
    }

    private static (IGitHubClientFactory ClientFactory, ICheckRunsClient CheckRuns, ICommitStatusClient Statuses)
        CreateClientFactory()
    {
        var checkRuns = Substitute.For<ICheckRunsClient>();
        var checks = Substitute.For<IChecksClient>();
        checks.Run.Returns(checkRuns);

        var statuses = Substitute.For<ICommitStatusClient>();
        var repository = Substitute.For<IRepositoriesClient>();
        repository.Status.Returns(statuses);

        var gitHubClient = Substitute.For<IGitHubClient>();
        gitHubClient.Check.Returns(checks);
        gitHubClient.Repository.Returns(repository);

        var clientFactory = Substitute.For<IGitHubClientFactory>();
        clientFactory.CreateForInstallation("ghs_token").Returns(gitHubClient);

        return (clientFactory, checkRuns, statuses);
    }

    private static CheckRunsResponse CreateCheckRuns(params CheckRun[] runs) =>
        new(runs.Length, runs);

    private static CheckRun CreateCheckRun(
        string name,
        CheckConclusion conclusion,
        CheckStatus status = CheckStatus.Completed) =>
        new(
            id: 1,
            headSha: "head-sha",
            externalId: null!,
            url: $"https://api.github.com/repos/octo/repo/check-runs/{name}",
            htmlUrl: $"https://github.com/octo/repo/runs/{name}",
            detailsUrl: null!,
            status: status,
            conclusion: conclusion,
            startedAt: DateTimeOffset.UtcNow.AddMinutes(-2),
            completedAt: status == CheckStatus.Completed ? DateTimeOffset.UtcNow : null,
            output: null!,
            name: name,
            checkSuite: null!,
            app: null!,
            pullRequests: []);

    private static CombinedCommitStatus CreateCombinedStatus(params CommitStatus[] statuses) =>
        new(
            state: statuses.Any(status => status.State.Value is CommitState.Error or CommitState.Failure)
                ? CommitState.Failure
                : CommitState.Success,
            sha: "head-sha",
            totalCount: statuses.Length,
            statuses: statuses,
            repository: null!);

    private static CommitStatus CreateCommitStatus(
        string context,
        CommitState state,
        string? description = null) =>
        new(
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            updatedAt: DateTimeOffset.UtcNow,
            state: state,
            targetUrl: null!,
            description: description,
            context: context,
            id: 1,
            nodeId: "node",
            url: $"https://api.github.com/repos/octo/repo/statuses/{context}",
            creator: null!);
}
