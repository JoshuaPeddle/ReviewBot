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
    public async Task FetchAsyncRetriesOnceWhenGitHubRateLimitIsExceeded()
    {
        var pullRequests = Substitute.For<IPullRequestsClient>();
        var clientFactory = CreateClientFactory(pullRequests);
        pullRequests.Get("octo", "repo", 42)
            .Returns(
                _ => Task.FromException<PullRequest>(CreateRateLimitExceeded()),
                _ => Task.FromResult(CreatePullRequest()));
        pullRequests.Files("octo", "repo", 42, Arg.Any<ApiOptions>())
            .Returns(CreateFiles(1, 1));
        var fetcher = new PullRequestFetcher(clientFactory);

        var snapshot = await fetcher.FetchAsync("octo", "repo", 42, "ghs_token", CancellationToken.None);

        snapshot.Files.Should().ContainSingle();
        await pullRequests.Received(2).Get("octo", "repo", 42);
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

    [Fact]
    public async Task FetchMetadataAsyncReturnsTitleBodyAndShasWithoutFetchingFiles()
    {
        var pullRequests = Substitute.For<IPullRequestsClient>();
        var clientFactory = CreateClientFactory(pullRequests);
        pullRequests.Get("octo", "repo", 42).Returns(CreatePullRequest());
        var fetcher = new PullRequestFetcher(clientFactory);

        var metadata = await fetcher.FetchMetadataAsync("octo", "repo", 42, "ghs_token", CancellationToken.None);

        metadata.Title.Should().Be("Add review bot");
        metadata.Body.Should().Be("PR body");
        metadata.BaseSha.Should().Be("base-sha");
        metadata.HeadSha.Should().Be("head-sha");
        await pullRequests.DidNotReceive().Files(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<ApiOptions>());
    }

    [Fact]
    public async Task FetchFilesAsyncWithNullAllowlistBehavesLikePagedFetch()
    {
        var pullRequests = Substitute.For<IPullRequestsClient>();
        var clientFactory = CreateClientFactory(pullRequests);
        pullRequests.Files("octo", "repo", 42, Arg.Any<ApiOptions>()).Returns([
            CreateFile("src/a.cs", "modified", 1, 0, "@@ -1 +1 @@\n+new\n"),
            CreateFile("src/b.cs", "added", 1, 0, "@@ -0,0 +1 @@\n+one\n"),
        ]);
        var fetcher = new PullRequestFetcher(clientFactory);

        var files = await fetcher.FetchFilesAsync("octo", "repo", 42, "ghs_token", maxFiles: 10, pathAllowlist: null, CancellationToken.None);

        files.Should().HaveCount(2);
        files.Select(f => f.Path).Should().BeEquivalentTo(["src/a.cs", "src/b.cs"]);
    }

    [Fact]
    public async Task FetchFilesAsyncWithAllowlistPagesUntilAllowlistedPathsFound()
    {
        var pullRequests = Substitute.For<IPullRequestsClient>();
        var clientFactory = CreateClientFactory(pullRequests);
        // Page 1: 30 files, none in allowlist
        pullRequests
            .Files("octo", "repo", 42, Arg.Is<ApiOptions>(o => o.StartPage == 1))
            .Returns(CreateFiles(1, 30));
        // Page 2: the allowlisted file is here (beyond first maxFiles=5 window)
        pullRequests
            .Files("octo", "repo", 42, Arg.Is<ApiOptions>(o => o.StartPage == 2))
            .Returns([CreateFile("delta/target.cs", "modified", 1, 0, "@@ -1 +1 @@\n+fix\n")]);
        var fetcher = new PullRequestFetcher(clientFactory);

        var allowlist = new HashSet<string>(StringComparer.Ordinal) { "delta/target.cs" };
        var files = await fetcher.FetchFilesAsync("octo", "repo", 42, "ghs_token", maxFiles: 5, pathAllowlist: allowlist, CancellationToken.None);

        // The allowlisted file was on page 2, which is beyond maxFiles=5 of the first page
        files.Should().ContainSingle();
        files[0].Path.Should().Be("delta/target.cs");
        // Both pages were fetched to find the allowlisted file
        await pullRequests.Received(1).Files("octo", "repo", 42, Arg.Is<ApiOptions>(o => o.StartPage == 1));
        await pullRequests.Received(1).Files("octo", "repo", 42, Arg.Is<ApiOptions>(o => o.StartPage == 2));
    }

    [Fact]
    public async Task FetchFilesAsyncWithAllowlistAppliesMaxFilesCapOnResult()
    {
        var pullRequests = Substitute.For<IPullRequestsClient>();
        var clientFactory = CreateClientFactory(pullRequests);
        pullRequests
            .Files("octo", "repo", 42, Arg.Any<ApiOptions>())
            .Returns([
                CreateFile("a.cs", "modified", 1, 0, "@@ -1 +1 @@\n+a\n"),
                CreateFile("b.cs", "modified", 1, 0, "@@ -1 +1 @@\n+b\n"),
                CreateFile("c.cs", "modified", 1, 0, "@@ -1 +1 @@\n+c\n"),
            ]);
        var fetcher = new PullRequestFetcher(clientFactory);

        var allowlist = new HashSet<string>(StringComparer.Ordinal) { "a.cs", "b.cs", "c.cs" };
        var files = await fetcher.FetchFilesAsync("octo", "repo", 42, "ghs_token", maxFiles: 2, pathAllowlist: allowlist, CancellationToken.None);

        files.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetChangedFilesSinceAsyncReturnsFilePathsFromCompareResult()
    {
        var (clientFactory, commits) = CreateClientFactoryWithCommits();
        var compareResult = CreateCompareResult(
            CreateCommitFile("src/a.cs"),
            CreateCommitFile("src/b.cs"));
        commits.Compare("octo", "repo", "base-sha", "head-sha").Returns(Task.FromResult(compareResult));
        var fetcher = new PullRequestFetcher(clientFactory);

        var result = await fetcher.GetChangedFilesSinceAsync("octo", "repo", "base-sha", "head-sha", "ghs_token", CancellationToken.None);

        result.Paths.Should().BeEquivalentTo(["src/a.cs", "src/b.cs"]);
        result.IsComplete.Should().BeTrue();
        await commits.Received(1).Compare("octo", "repo", "base-sha", "head-sha");
    }

    [Fact]
    public async Task GetChangedFilesSinceAsyncMarksExactly300FilesAsIncomplete()
    {
        var (clientFactory, commits) = CreateClientFactoryWithCommits();
        var files = Enumerable.Range(0, 300).Select(i => CreateCommitFile($"src/file-{i}.cs")).ToArray();
        commits.Compare("octo", "repo", "base-sha", "head-sha").Returns(Task.FromResult(CreateCompareResult(files)));
        var fetcher = new PullRequestFetcher(clientFactory);

        var result = await fetcher.GetChangedFilesSinceAsync("octo", "repo", "base-sha", "head-sha", "ghs_token", CancellationToken.None);

        result.Paths.Should().HaveCount(300);
        result.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task GetChangedFilesSinceAsyncMarksFewerThan300FilesAsComplete()
    {
        var (clientFactory, commits) = CreateClientFactoryWithCommits();
        var files = Enumerable.Range(0, 42).Select(i => CreateCommitFile($"src/file-{i}.cs")).ToArray();
        commits.Compare("octo", "repo", "base-sha", "head-sha").Returns(Task.FromResult(CreateCompareResult(files)));
        var fetcher = new PullRequestFetcher(clientFactory);

        var result = await fetcher.GetChangedFilesSinceAsync("octo", "repo", "base-sha", "head-sha", "ghs_token", CancellationToken.None);

        result.Paths.Should().HaveCount(42);
        result.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task GetChangedFilesSinceAsyncThrowsNotFoundExceptionWhenGitHubReturns404()
    {
        var (clientFactory, commits) = CreateClientFactoryWithCommits();
        commits.Compare("octo", "repo", "old-sha", "new-sha")
            .Returns<CompareResult>(_ => throw new NotFoundException("Not Found", System.Net.HttpStatusCode.NotFound));
        var fetcher = new PullRequestFetcher(clientFactory);

        var act = () => fetcher.GetChangedFilesSinceAsync("octo", "repo", "old-sha", "new-sha", "ghs_token", CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    private static IGitHubClientFactory CreateClientFactory(IPullRequestsClient pullRequests)
    {
        var gitHubClient = Substitute.For<IGitHubClient>();
        gitHubClient.PullRequest.Returns(pullRequests);

        var clientFactory = Substitute.For<IGitHubClientFactory>();
        clientFactory.CreateForInstallation("ghs_token").Returns(gitHubClient);

        return clientFactory;
    }

    private static (IGitHubClientFactory ClientFactory, IRepositoryCommitsClient Commits) CreateClientFactoryWithCommits()
    {
        var commits = Substitute.For<IRepositoryCommitsClient>();
        var repository = Substitute.For<IRepositoriesClient>();
        repository.Commit.Returns(commits);

        var gitHubClient = Substitute.For<IGitHubClient>();
        gitHubClient.Repository.Returns(repository);

        var clientFactory = Substitute.For<IGitHubClientFactory>();
        clientFactory.CreateForInstallation("ghs_token").Returns(gitHubClient);

        return (clientFactory, commits);
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

    private static RateLimitExceededException CreateRateLimitExceeded()
    {
        var response = Substitute.For<IResponse>();
        response.ApiInfo.Returns(new ApiInfo(
            new Dictionary<string, Uri>(),
            [],
            [],
            string.Empty,
            new RateLimit(5000, 0, DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds()),
            TimeSpan.Zero));

        return new RateLimitExceededException(response);
    }

    private static GitHubCommitFile CreateCommitFile(string filename) => new(
        filename,
        additions: 1,
        deletions: 0,
        changes: 1,
        status: "modified",
        blobUrl: $"https://github.com/octo/repo/blob/sha/{filename}",
        contentsUrl: $"https://api.github.com/repos/octo/repo/contents/{filename}",
        rawUrl: $"https://raw.githubusercontent.com/octo/repo/sha/{filename}",
        sha: "file-sha",
        patch: null,
        previousFileName: null);

    private static CompareResult CreateCompareResult(params GitHubCommitFile[] files) => new(
        url: "https://api.github.com/repos/octo/repo/compare/base...head",
        htmlUrl: "https://github.com/octo/repo/compare/base...head",
        permalinkUrl: "https://github.com/octo/repo/compare/base...head",
        diffUrl: "https://github.com/octo/repo/compare/base...head.diff",
        patchUrl: "https://github.com/octo/repo/compare/base...head.patch",
        baseCommit: null!,
        mergeBaseCommit: null!,
        status: "ahead",
        aheadBy: files.Length,
        behindBy: 0,
        totalCommits: files.Length,
        commits: [],
        files: files);
}
