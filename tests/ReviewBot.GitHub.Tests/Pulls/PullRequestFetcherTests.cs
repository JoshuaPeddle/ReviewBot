using FluentAssertions;
using NSubstitute;
using Octokit;
using ReviewBot.Core.Domain;
using ReviewBot.GitHub.Pulls;

namespace ReviewBot.GitHub.Tests.Pulls;

public class PullRequestFetcherTests
{
    [Fact]
    public async Task FetchAsyncReturnsPullRequestSnapshotWithMappedFiles()
    {
        var pullRequests = Substitute.For<IPullRequestsClient>();
        var clientFactory = CreateClientFactory(pullRequests);
        pullRequests.Get("octo", "repo", 42).Returns(CreatePullRequest());
        pullRequests.Files("octo", "repo", 42, Arg.Any<ApiOptions>()).Returns([
            CreateFile(
                "src/a.cs",
                "modified",
                2,
                1,
                """
                @@ -1,2 +1,3 @@
                 context
                -old
                +new
                +extra
                """),
            CreateFile(
                "src/new.cs",
                "added",
                2,
                0,
                """
                @@ -0,0 +1,2 @@
                +one
                +two
                """),
        ]);
        var fetcher = new PullRequestFetcher(clientFactory);

        var snapshot = await fetcher.FetchAsync("octo", "repo", 42, "ghs_token", CancellationToken.None);

        snapshot.Title.Should().Be("Add review bot");
        snapshot.Body.Should().Be("PR body");
        snapshot.BaseSha.Should().Be("base-sha");
        snapshot.HeadSha.Should().Be("head-sha");
        snapshot.Files.Should().HaveCount(2);

        snapshot.Files[0].Should().BeEquivalentTo(new
        {
            Path = "src/a.cs",
            AdditionsCount = 2L,
            DeletionsCount = 1L,
            Status = FileChangeStatus.Modified,
        });
        snapshot.Files[0].CommentableLines.Should().BeEquivalentTo([1, 2, 3]);

        snapshot.Files[1].Should().BeEquivalentTo(new
        {
            Path = "src/new.cs",
            AdditionsCount = 2L,
            DeletionsCount = 0L,
            Status = FileChangeStatus.Added,
        });
        snapshot.Files[1].CommentableLines.Should().BeEquivalentTo([1, 2]);
    }

    [Fact]
    public async Task FetchAsyncOmitsFilesWithNullPatch()
    {
        var pullRequests = Substitute.For<IPullRequestsClient>();
        var clientFactory = CreateClientFactory(pullRequests);
        pullRequests.Get("octo", "repo", 42).Returns(CreatePullRequest());
        pullRequests.Files("octo", "repo", 42, Arg.Any<ApiOptions>()).Returns([
            CreateFile("large.bin", "modified", 10, 5, null),
            CreateFile(
                "src/a.cs",
                "modified",
                1,
                0,
                """
                @@ -1 +1 @@
                +new
                """),
        ]);
        var fetcher = new PullRequestFetcher(clientFactory);

        var snapshot = await fetcher.FetchAsync("octo", "repo", 42, "ghs_token", CancellationToken.None);

        snapshot.Files.Should().ContainSingle();
        snapshot.Files[0].Path.Should().Be("src/a.cs");
    }

    [Fact]
    public async Task FetchAsyncFetchesMultiplePagesUntilGitHubReturnsShortPage()
    {
        var pullRequests = Substitute.For<IPullRequestsClient>();
        var clientFactory = CreateClientFactory(pullRequests);
        pullRequests.Get("octo", "repo", 42).Returns(CreatePullRequest());
        pullRequests.Files(
                "octo",
                "repo",
                42,
                Arg.Is<ApiOptions>(options => options.StartPage == 1 && options.PageSize == 30 && options.PageCount == 1))
            .Returns(CreateFiles(1, 30));
        pullRequests.Files(
                "octo",
                "repo",
                42,
                Arg.Is<ApiOptions>(options => options.StartPage == 2 && options.PageSize == 20 && options.PageCount == 1))
            .Returns(CreateFiles(31, 1));
        var fetcher = new PullRequestFetcher(clientFactory);

        var snapshot = await fetcher.FetchAsync("octo", "repo", 42, "ghs_token", CancellationToken.None);

        snapshot.Files.Should().HaveCount(31);
        snapshot.Files.Select(file => file.Path).Should().Contain(["src/file-1.cs", "src/file-31.cs"]);
        await pullRequests.Received(1).Files(
            "octo",
            "repo",
            42,
            Arg.Is<ApiOptions>(options => options.StartPage == 1));
        await pullRequests.Received(1).Files(
            "octo",
            "repo",
            42,
            Arg.Is<ApiOptions>(options => options.StartPage == 2));
    }

    [Fact]
    public async Task FetchAsyncCapsChangedFilesAtDefaultMaxFiles()
    {
        var pullRequests = Substitute.For<IPullRequestsClient>();
        var clientFactory = CreateClientFactory(pullRequests);
        pullRequests.Get("octo", "repo", 42).Returns(CreatePullRequest());
        pullRequests.Files(
                "octo",
                "repo",
                42,
                Arg.Is<ApiOptions>(options => options.StartPage == 1 && options.PageSize == 30))
            .Returns(CreateFiles(1, 30));
        pullRequests.Files(
                "octo",
                "repo",
                42,
                Arg.Is<ApiOptions>(options => options.StartPage == 2 && options.PageSize == 20))
            .Returns(CreateFiles(31, 20));
        var fetcher = new PullRequestFetcher(clientFactory);

        var snapshot = await fetcher.FetchAsync("octo", "repo", 42, "ghs_token", CancellationToken.None);

        snapshot.Files.Should().HaveCount(ReviewConfig.Default.Review.MaxFiles);
        snapshot.Files.Last().Path.Should().Be("src/file-50.cs");
        await pullRequests.DidNotReceive().Files(
            "octo",
            "repo",
            42,
            Arg.Is<ApiOptions>(options => options.StartPage == 3));
    }

    [Fact]
    public async Task FetchAsyncUsesPerCallMaxFilesOverride()
    {
        var pullRequests = Substitute.For<IPullRequestsClient>();
        var clientFactory = CreateClientFactory(pullRequests);
        pullRequests.Get("octo", "repo", 42).Returns(CreatePullRequest());
        pullRequests.Files(
                "octo",
                "repo",
                42,
                Arg.Is<ApiOptions>(options => options.StartPage == 1 && options.PageSize == 3))
            .Returns(CreateFiles(1, 3));
        var fetcher = new PullRequestFetcher(clientFactory);

        var snapshot = await fetcher.FetchAsync("octo", "repo", 42, "ghs_token", maxFiles: 3, CancellationToken.None);

        snapshot.Files.Should().HaveCount(3);
        snapshot.Files.Last().Path.Should().Be("src/file-3.cs");
        await pullRequests.Received(1).Files(
            "octo",
            "repo",
            42,
            Arg.Is<ApiOptions>(options => options.PageSize == 3));
        await pullRequests.DidNotReceive().Files(
            "octo",
            "repo",
            42,
            Arg.Is<ApiOptions>(options => options.StartPage == 2));
    }

    [Fact]
    public void CreateForInstallationBuildsAnOctokitClientWithTokenCredentials()
    {
        var factory = new OctokitGitHubClientFactory();

        var client = factory.CreateForInstallation("ghs_token");

        var githubClient = client.Should().BeOfType<GitHubClient>().Subject;
        githubClient.Credentials.Password.Should().Be("ghs_token");
        githubClient.Credentials.AuthenticationType.Should().Be(AuthenticationType.Oauth);
    }

    private static IGitHubClientFactory CreateClientFactory(IPullRequestsClient pullRequests)
    {
        var gitHubClient = Substitute.For<IGitHubClient>();
        gitHubClient.PullRequest.Returns(pullRequests);

        var clientFactory = Substitute.For<IGitHubClientFactory>();
        clientFactory.CreateForInstallation("ghs_token").Returns(gitHubClient);

        return clientFactory;
    }

    private static PullRequest CreatePullRequest() => new(
        1,
        "node-id",
        "https://api.github.com/repos/octo/repo/pulls/42",
        "https://github.com/octo/repo/pull/42",
        "https://github.com/octo/repo/pull/42.diff",
        "https://github.com/octo/repo/pull/42.patch",
        "https://api.github.com/repos/octo/repo/issues/42",
        "https://api.github.com/repos/octo/repo/statuses/head-sha",
        42,
        ItemState.Open,
        "Add review bot",
        "PR body",
        new DateTimeOffset(2026, 5, 23, 12, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 5, 23, 12, 1, 0, TimeSpan.Zero),
        null,
        null,
        CreateGitReference("head-sha"),
        CreateGitReference("base-sha"),
        null!,
        null!,
        [],
        false,
        null,
        null,
        null!,
        "merge-sha",
        0,
        1,
        3,
        1,
        2,
        null!,
        false,
        null,
        [],
        [],
        [],
        null);

    private static GitReference CreateGitReference(string sha) => new(
        "ref-node-id",
        $"https://api.github.com/repos/octo/repo/git/ref/{sha}",
        $"octo:{sha}",
        $"refs/heads/{sha}",
        sha,
        null!,
        null!);

    private static PullRequestFile CreateFile(
        string path,
        string status,
        int additions,
        int deletions,
        string? patch) => new(
            "file-sha",
            path,
            status,
            additions,
            deletions,
            additions + deletions,
            $"https://github.com/octo/repo/blob/head-sha/{path}",
            $"https://raw.githubusercontent.com/octo/repo/head-sha/{path}",
            $"https://api.github.com/repos/octo/repo/contents/{path}",
            patch!,
            null!);

    private static IReadOnlyList<PullRequestFile> CreateFiles(int start, int count) =>
        Enumerable.Range(start, count)
            .Select(index => CreateFile(
                $"src/file-{index}.cs",
                "modified",
                1,
                0,
                $$"""
                @@ -{{index}} +{{index}} @@
                +line {{index}}
                """))
            .ToArray();
}
