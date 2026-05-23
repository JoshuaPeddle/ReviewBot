using FluentAssertions;
using ReviewBot.Grounding.Workspace;

namespace ReviewBot.Grounding.Tests.Workspace;

public class GitWorkspaceTests
{
    [Fact]
    public async Task DisposeAsyncRemovesLocalDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"reviewbot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        Directory.Exists(dir).Should().BeTrue();

        var workspace = new GitWorkspace(dir);
        await workspace.DisposeAsync();

        Directory.Exists(dir).Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsyncIsIdempotentWhenDirectoryAlreadyGone()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"reviewbot-test-{Guid.NewGuid():N}");
        // Never created — should not throw
        var workspace = new GitWorkspace(dir);

        var act = () => workspace.DisposeAsync().AsTask();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsyncRemovesDirectoryWithContents()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"reviewbot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "file.txt"), "hello");
        File.WriteAllText(Path.Combine(dir, "sub", "nested.txt"), "world");

        var workspace = new GitWorkspace(dir);
        await workspace.DisposeAsync();

        Directory.Exists(dir).Should().BeFalse();
    }

    [Fact]
    public void LocalPathReturnedIsThePathProvidedAtConstruction()
    {
        var path = "/some/path/to/workspace";
        var workspace = new GitWorkspace(path);

        workspace.LocalPath.Should().Be(path);
    }
}
