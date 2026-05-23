using System.Text;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Octokit;
using ReviewBot.GitHub.Pulls;
using ReviewBot.Grounding.Detection;

namespace ReviewBot.Grounding.Tests.Detection;

public class GitHubRepoContentReaderTests
{
    private const string Owner = "acme";
    private const string Repo = "myapp";
    private const string Token = "ghs_token";
    private const string Sha = "abc1234";

    [Fact]
    public async Task ListRootFilesAsyncReturnsBlobNamesAtRootLevel()
    {
        var treesClient = Substitute.For<ITreesClient>();
        treesClient.Get(Owner, Repo, Sha).Returns(
            CreateTreeResponse([
                CreateTreeItem("README.md", TreeType.Blob),
                CreateTreeItem("src", TreeType.Tree),
                CreateTreeItem("Directory.Build.props", TreeType.Blob),
                CreateTreeItem("MyApp.sln", TreeType.Blob),
            ]));

        var reader = CreateReader(treesClient: treesClient);

        var files = await reader.ListRootFilesAsync(Sha, CancellationToken.None);

        files.Should().BeEquivalentTo(["README.md", "Directory.Build.props", "MyApp.sln"]);
    }

    [Fact]
    public async Task ListRootFilesAsyncPassesCorrectShaToTreeCall()
    {
        const string specificSha = "deadbeef";
        var treesClient = Substitute.For<ITreesClient>();
        treesClient.Get(Owner, Repo, specificSha).Returns(CreateTreeResponse([]));

        var reader = CreateReader(treesClient: treesClient);

        await reader.ListRootFilesAsync(specificSha, CancellationToken.None);

        await treesClient.Received(1).Get(Owner, Repo, specificSha);
    }

    [Fact]
    public async Task TryReadFileAsyncDecodesBase64Content()
    {
        const string expectedContent = "Hello, world!";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(expectedContent));

        var contentsClient = Substitute.For<IRepositoryContentsClient>();
        contentsClient.GetAllContentsByRef(Owner, Repo, "README.md", Sha)
            .Returns([CreateRepositoryContent("README.md", encoded)]);

        var reader = CreateReader(contentsClient: contentsClient);

        var result = await reader.TryReadFileAsync("README.md", Sha, CancellationToken.None);

        result.Should().Be(expectedContent);
    }

    [Fact]
    public async Task TryReadFileAsyncReturnsNullWhenFileNotFound()
    {
        // Create exception before NSubstitute setup to avoid call-recording interference.
        var notFound = CreateNotFoundException();
        var contentsClient = Substitute.For<IRepositoryContentsClient>();
        contentsClient.GetAllContentsByRef(Owner, Repo, "missing.txt", Sha)
            .ThrowsAsync(notFound);

        var reader = CreateReader(contentsClient: contentsClient);

        var result = await reader.TryReadFileAsync("missing.txt", Sha, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryReadFileAsyncPassesCorrectPathAndShaToContentCall()
    {
        const string path = "src/MyApp.csproj";
        const string specificSha = "cafebabe";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("<Project/>"));

        var contentsClient = Substitute.For<IRepositoryContentsClient>();
        contentsClient.GetAllContentsByRef(Owner, Repo, path, specificSha)
            .Returns([CreateRepositoryContent(path, encoded)]);

        var reader = CreateReader(contentsClient: contentsClient);

        await reader.TryReadFileAsync(path, specificSha, CancellationToken.None);

        await contentsClient.Received(1).GetAllContentsByRef(Owner, Repo, path, specificSha);
    }

    [Fact]
    public async Task TryReadFileAsyncReturnsNullWhenEncodedContentIsNull()
    {
        var content = Substitute.For<RepositoryContent>();
        content.EncodedContent.Returns((string?)null);

        var contentsClient = Substitute.For<IRepositoryContentsClient>();
        contentsClient.GetAllContentsByRef(Owner, Repo, "file.txt", Sha)
            .Returns([content]);

        var reader = CreateReader(contentsClient: contentsClient);

        var result = await reader.TryReadFileAsync("file.txt", Sha, CancellationToken.None);

        result.Should().BeNull();
    }

    private static GitHubRepoContentReader CreateReader(
        ITreesClient? treesClient = null,
        IRepositoryContentsClient? contentsClient = null)
    {
        var gitDatabase = Substitute.For<IGitDatabaseClient>();
        gitDatabase.Tree.Returns(treesClient ?? Substitute.For<ITreesClient>());

        var repoClient = Substitute.For<IRepositoriesClient>();
        repoClient.Content.Returns(contentsClient ?? Substitute.For<IRepositoryContentsClient>());

        var gitHubClient = Substitute.For<IGitHubClient>();
        gitHubClient.Git.Returns(gitDatabase);
        gitHubClient.Repository.Returns(repoClient);

        var clientFactory = Substitute.For<IGitHubClientFactory>();
        clientFactory.CreateForInstallation(Token).Returns(gitHubClient);

        return new GitHubRepoContentReader(clientFactory, Owner, Repo, Token);
    }

    private static TreeResponse CreateTreeResponse(IReadOnlyList<TreeItem> items) =>
        new("tree-sha", "https://api.github.com/repos/acme/myapp/git/trees/tree-sha", items, false);

    private static TreeItem CreateTreeItem(string path, TreeType type) =>
        new(path, "100644", type, 0, "item-sha", "https://api.github.com/repos/acme/myapp/git/trees/item-sha");

    private static RepositoryContent CreateRepositoryContent(string path, string encodedContent)
    {
        var name = Path.GetFileName(path);
        return new RepositoryContent(
            name,
            path,
            "file-sha",
            0,
            ContentType.File,
            $"https://raw.githubusercontent.com/acme/myapp/main/{path}",
            $"https://api.github.com/repos/acme/myapp/contents/{path}",
            $"https://api.github.com/repos/acme/myapp/git/blobs/file-sha",
            $"https://github.com/acme/myapp/blob/main/{path}",
            "base64",
            encodedContent,
            null,
            null);
    }

    private static NotFoundException CreateNotFoundException()
    {
        var response = Substitute.For<IResponse>();
        response.StatusCode.Returns(System.Net.HttpStatusCode.NotFound);
        return new NotFoundException(response);
    }
}
