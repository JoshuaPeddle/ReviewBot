using FluentAssertions;
using NSubstitute;
using ReviewBot.Grounding.Workspace;

namespace ReviewBot.Grounding.Tests;

public class SharedWorkspaceTests
{
    private static WorkspaceRequest Request(string sha = "sha1", string url = "https://github.com/acme/repo.git") =>
        new(CloneUrl: url, Sha: sha, InstallationToken: "token");

    [Fact]
    public async Task GetOrCreateAsyncClonesOncePerKeyAndReturnsTheSameWorkspace()
    {
        var workspace = Substitute.For<IWorkspace>();
        var factory = Substitute.For<IWorkspaceFactory>();
        factory.CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>()).Returns(workspace);

        await using var shared = new SharedWorkspace(factory);

        var first = await shared.GetOrCreateAsync(Request(), CancellationToken.None);
        var second = await shared.GetOrCreateAsync(Request(), CancellationToken.None);

        first.Should().BeSameAs(workspace);
        second.Should().BeSameAs(first);
        await factory.Received(1).CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrCreateAsyncClonesSeparatelyForDifferentKeys()
    {
        var workspaceA = Substitute.For<IWorkspace>();
        var workspaceB = Substitute.For<IWorkspace>();
        var factory = Substitute.For<IWorkspaceFactory>();
        factory.CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<WorkspaceRequest>().Sha == "a" ? workspaceA : workspaceB);

        await using var shared = new SharedWorkspace(factory);

        var resultA = await shared.GetOrCreateAsync(Request(sha: "a"), CancellationToken.None);
        var resultB = await shared.GetOrCreateAsync(Request(sha: "b"), CancellationToken.None);

        resultA.Should().BeSameAs(workspaceA);
        resultB.Should().BeSameAs(workspaceB);
        await factory.Received(2).CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisposeAsyncDisposesEachClonedWorkspaceExactlyOnce()
    {
        var workspace = Substitute.For<IWorkspace>();
        var factory = Substitute.For<IWorkspaceFactory>();
        factory.CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>()).Returns(workspace);

        var shared = new SharedWorkspace(factory);
        await shared.GetOrCreateAsync(Request(), CancellationToken.None);
        await shared.GetOrCreateAsync(Request(), CancellationToken.None);

        await shared.DisposeAsync();
        await shared.DisposeAsync(); // idempotent

        await workspace.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsyncDoesNotCloneWhenNothingWasAcquired()
    {
        var factory = Substitute.For<IWorkspaceFactory>();

        var shared = new SharedWorkspace(factory);
        await shared.DisposeAsync();

        await factory.DidNotReceive().CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrCreateAsyncAfterDisposeThrows()
    {
        var factory = Substitute.For<IWorkspaceFactory>();
        var shared = new SharedWorkspace(factory);
        await shared.DisposeAsync();

        var act = async () => await shared.GetOrCreateAsync(Request(), CancellationToken.None);

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task GetOrCreateAsyncEvictsAFailedCloneSoLaterConsumersRetry()
    {
        var workspace = Substitute.For<IWorkspace>();
        var factory = Substitute.For<IWorkspaceFactory>();
        factory.CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromException<IWorkspace>(new InvalidOperationException("transient clone failure")),
                _ => Task.FromResult(workspace));

        await using var shared = new SharedWorkspace(factory);

        var firstAttempt = async () => await shared.GetOrCreateAsync(Request(), CancellationToken.None);
        await firstAttempt.Should().ThrowAsync<InvalidOperationException>();

        var retried = await shared.GetOrCreateAsync(Request(), CancellationToken.None);

        retried.Should().BeSameAs(workspace);
        await factory.Received(2).CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>());
    }
}
