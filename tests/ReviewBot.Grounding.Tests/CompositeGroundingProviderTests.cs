using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Otel;
using ReviewBot.GitHub.Checks;
using ReviewBot.GitHub.Pulls;
using ReviewBot.Grounding.Build;
using ReviewBot.Grounding.Detection;
using ReviewBot.Grounding.Workspace;

namespace ReviewBot.Grounding.Tests;

public class CompositeGroundingProviderTests
{
    private static readonly GroundingRequest Request = new(
        Owner: "acme",
        Repo: "myapp",
        HeadSha: "abc1234",
        InstallationToken: "ghs_token",
        Config: GroundingConfig.Default);

    [Fact]
    public async Task GetContextAsyncReturnsEmptyContextWhenGroundingDisabled()
    {
        var detector = Substitute.For<ILanguageDetector>();
        var reader = Substitute.For<IRepoContentReader>();
        var provider = CreateProvider([detector], reader);
        var disabledRequest = Request with { Config = GroundingConfig.Default with { Enabled = false } };

        var ctx = await provider.GetContextAsync(disabledRequest, CancellationToken.None);

        ctx.Language.Should().BeNull();
        ctx.Build.Should().BeNull();
        ctx.Tests.Should().BeNull();
        await reader.DidNotReceive().ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        detector.DidNotReceive().CanDetect(Arg.Any<IReadOnlyList<string>>());
    }

    [Fact]
    public async Task GetContextAsyncFirstMatchingDetectorWins()
    {
        var dotnetDetector = Substitute.For<ILanguageDetector>();
        dotnetDetector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        var expectedMetadata = new LanguageMetadata("dotnet", "10.0", null, []);
        dotnetDetector.ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(expectedMetadata);

        var pythonDetector = Substitute.For<ILanguageDetector>();
        pythonDetector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["MyApp.csproj"]);

        var provider = CreateProvider([dotnetDetector, pythonDetector], reader);

        var ctx = await provider.GetContextAsync(Request, CancellationToken.None);

        ctx.Language.Should().BeSameAs(expectedMetadata);
        pythonDetector.DidNotReceive().CanDetect(Arg.Any<IReadOnlyList<string>>());
    }

    [Fact]
    public async Task GetContextAsyncSkipsNonMatchingDetectors()
    {
        var dotnetDetector = Substitute.For<ILanguageDetector>();
        dotnetDetector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(false);

        var pythonDetector = Substitute.For<ILanguageDetector>();
        pythonDetector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        var expected = new LanguageMetadata("python", "3.12", null, []);
        pythonDetector.ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["pyproject.toml"]);

        var provider = CreateProvider([dotnetDetector, pythonDetector], reader);

        var ctx = await provider.GetContextAsync(Request, CancellationToken.None);

        ctx.Language.Should().BeSameAs(expected);
        dotnetDetector.Received(1).CanDetect(Arg.Any<IReadOnlyList<string>>());
        await pythonDetector.Received(1).ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetContextAsyncReturnsEmptyContextWhenNoDetectorMatches()
    {
        var detector = Substitute.For<ILanguageDetector>();
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(false);

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["unknown.xyz"]);

        var provider = CreateProvider([detector], reader);

        var ctx = await provider.GetContextAsync(Request, CancellationToken.None);

        ctx.Language.Should().BeNull();
        ctx.Build.Should().BeNull();
        ctx.Tests.Should().BeNull();
    }

    [Fact]
    public async Task GetContextAsyncReturnsEmptyContextAndDoesNotRethrowWhenDetectorThrows()
    {
        var detector = Substitute.For<ILanguageDetector>();
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        detector.ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("detector boom"));

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["MyApp.csproj"]);

        var provider = CreateProvider([detector], reader);

        var ctx = await provider.GetContextAsync(Request, CancellationToken.None);

        ctx.Language.Should().BeNull();
    }

    [Fact]
    public async Task GetContextAsyncReturnsEmptyContextAndDoesNotRethrowWhenReaderThrows()
    {
        var detector = Substitute.For<ILanguageDetector>();
        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network error"));

        var provider = CreateProvider([detector], reader);

        var ctx = await provider.GetContextAsync(Request, CancellationToken.None);

        ctx.Language.Should().BeNull();
        detector.DidNotReceive().CanDetect(Arg.Any<IReadOnlyList<string>>());
    }

    [Fact]
    public void DiRegistrationResolvesGroundingProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGitHubClientFactory>(Substitute.For<IGitHubClientFactory>());
        services.AddLogging();
        services.AddGrounding();

        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IGroundingProvider>().Should().BeOfType<CompositeGroundingProvider>();
    }

    [Fact]
    public void DiRegistrationAddLanguageDetectorRegistersDetector()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGitHubClientFactory>(Substitute.For<IGitHubClientFactory>());
        services.AddLogging();
        services.AddGrounding()
            .AddLanguageDetector<StubLanguageDetector>();

        var provider = services.BuildServiceProvider();

        provider.GetServices<ILanguageDetector>().Should().ContainSingle()
            .Which.Should().BeOfType<StubLanguageDetector>();
    }

    [Fact]
    public async Task GetContextAsyncRunsBuildWhenBuildEnabledAndRunnerMatches()
    {
        var detector = Substitute.For<ILanguageDetector>();
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        var language = new LanguageMetadata("dotnet", "10.0", null, []);
        detector.ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(language);

        var runner = Substitute.For<IBuildRunner>();
        runner.LanguageId.Returns("dotnet");
        var buildResult = new BuildResult(true, 0, 0, "Build succeeded");
        runner.RunAsync(Arg.Any<string>(), Arg.Any<GroundingConfig>(), Arg.Any<CancellationToken>())
            .Returns(buildResult);

        var workspace = Substitute.For<IWorkspace>();
        workspace.LocalPath.Returns("/tmp/fake-workspace");
        workspace.DisposeAsync().Returns(ValueTask.CompletedTask);

        var workspaceFactory = Substitute.For<IWorkspaceFactory>();
        workspaceFactory.CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(workspace);

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["MyApp.csproj"]);

        var buildRequest = Request with { Config = GroundingConfig.Default with { Build = true } };
        var provider = CreateProviderWithBuild([detector], [runner], workspaceFactory, reader);

        var ctx = await provider.GetContextAsync(buildRequest, CancellationToken.None);

        ctx.Language.Should().BeSameAs(language);
        ctx.Build.Should().BeSameAs(buildResult);
        await runner.Received(1).RunAsync("/tmp/fake-workspace", Arg.Any<GroundingConfig>(), Arg.Any<CancellationToken>());
        await workspace.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task GetContextAsyncWithSharedWorkspaceBuildsButLeavesDisposalToTheScope()
    {
        var detector = Substitute.For<ILanguageDetector>();
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        var language = new LanguageMetadata("dotnet", "10.0", null, []);
        detector.ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(language);

        var runner = Substitute.For<IBuildRunner>();
        runner.LanguageId.Returns("dotnet");
        var buildResult = new BuildResult(true, 0, 0, "Build succeeded");
        runner.RunAsync(Arg.Any<string>(), Arg.Any<GroundingConfig>(), Arg.Any<CancellationToken>())
            .Returns(buildResult);

        var workspace = Substitute.For<IWorkspace>();
        workspace.LocalPath.Returns("/tmp/shared-workspace");
        workspace.DisposeAsync().Returns(ValueTask.CompletedTask);

        // Shared scope owns the clone; grounding's own factory must not be used.
        var sharedFactory = Substitute.For<IWorkspaceFactory>();
        sharedFactory.CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>()).Returns(workspace);
        var shared = new SharedWorkspace(sharedFactory);

        var ownFactory = Substitute.For<IWorkspaceFactory>();
        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["MyApp.csproj"]);

        var buildRequest = Request with { Config = GroundingConfig.Default with { Build = true } };
        var provider = CreateProviderWithBuild([detector], [runner], ownFactory, reader);

        var ctx = await provider.GetContextAsync(buildRequest, CancellationToken.None, shared);

        ctx.Build.Should().BeSameAs(buildResult);
        await runner.Received(1).RunAsync("/tmp/shared-workspace", Arg.Any<GroundingConfig>(), Arg.Any<CancellationToken>());
        // Grounding cloned through the shared scope, not its own factory, and did not dispose it.
        await ownFactory.DidNotReceive().CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>());
        await workspace.DidNotReceive().DisposeAsync();

        // The scope — not grounding — disposes the clone exactly once.
        await shared.DisposeAsync();
        await workspace.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task GetContextAsyncSkipsBuildWhenBuildDisabled()
    {
        var detector = Substitute.For<ILanguageDetector>();
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        detector.ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LanguageMetadata("dotnet", "10.0", null, []));

        var runner = Substitute.For<IBuildRunner>();
        runner.LanguageId.Returns("dotnet");

        var workspaceFactory = Substitute.For<IWorkspaceFactory>();

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["MyApp.csproj"]);

        // Build = false (default)
        var provider = CreateProviderWithBuild([detector], [runner], workspaceFactory, reader);

        var ctx = await provider.GetContextAsync(Request, CancellationToken.None);

        ctx.Build.Should().BeNull();
        await workspaceFactory.DidNotReceive().CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>());
        await runner.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<GroundingConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetContextAsyncReturnsBuildNullWhenWorkspaceCreationFails()
    {
        // With the parallel clone design, workspace creation failures are logged and return null —
        // the clone task completes with null rather than propagating an exception into the build path.
        var detector = Substitute.For<ILanguageDetector>();
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        detector.ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LanguageMetadata("dotnet", "10.0", null, []));

        var runner = Substitute.For<IBuildRunner>();
        runner.LanguageId.Returns("dotnet");

        var workspaceFactory = Substitute.For<IWorkspaceFactory>();
        workspaceFactory.CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("git fetch failed: repository not found"));

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["MyApp.csproj"]);

        var buildRequest = Request with { Config = GroundingConfig.Default with { Build = true } };
        var provider = CreateProviderWithBuild([detector], [runner], workspaceFactory, reader);

        var ctx = await provider.GetContextAsync(buildRequest, CancellationToken.None);

        ctx.Language.Should().NotBeNull();
        ctx.Build.Should().BeNull();
    }

    [Fact]
    public async Task GetContextAsyncReturnsBuildNullWhenNoRunnerMatchesLanguage()
    {
        // With the parallel clone design, the workspace clone starts as soon as Build=true is set,
        // before we know the language ID from metadata. If no runner matches the extracted language,
        // the workspace is disposed and Build is null.
        var detector = Substitute.For<ILanguageDetector>();
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        detector.ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LanguageMetadata("go", "1.23", null, []));

        var runner = Substitute.For<IBuildRunner>();
        runner.LanguageId.Returns("dotnet");

        var workspace = Substitute.For<IWorkspace>();
        workspace.LocalPath.Returns("/tmp/fake-workspace");
        workspace.DisposeAsync().Returns(ValueTask.CompletedTask);

        var workspaceFactory = Substitute.For<IWorkspaceFactory>();
        workspaceFactory.CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(workspace);

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["go.mod"]);

        var buildRequest = Request with { Config = GroundingConfig.Default with { Build = true } };
        var provider = CreateProviderWithBuild([detector], [runner], workspaceFactory, reader);

        var ctx = await provider.GetContextAsync(buildRequest, CancellationToken.None);

        ctx.Language.Should().NotBeNull();
        ctx.Build.Should().BeNull();
        await runner.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<GroundingConfig>(), Arg.Any<CancellationToken>());
        await workspace.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task GetContextAsyncDisposesWorkspaceEvenWhenRunnerFails()
    {
        var detector = Substitute.For<ILanguageDetector>();
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        detector.ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LanguageMetadata("dotnet", "10.0", null, []));

        var runner = Substitute.For<IBuildRunner>();
        runner.LanguageId.Returns("dotnet");
        runner.RunAsync(Arg.Any<string>(), Arg.Any<GroundingConfig>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("runner exploded"));

        var workspace = Substitute.For<IWorkspace>();
        workspace.LocalPath.Returns("/tmp/fake-workspace");
        workspace.DisposeAsync().Returns(ValueTask.CompletedTask);

        var workspaceFactory = Substitute.For<IWorkspaceFactory>();
        workspaceFactory.CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(workspace);

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["MyApp.csproj"]);

        var buildRequest = Request with { Config = GroundingConfig.Default with { Build = true } };
        var provider = CreateProviderWithBuild([detector], [runner], workspaceFactory, reader);

        var ctx = await provider.GetContextAsync(buildRequest, CancellationToken.None);

        ctx.Build!.Success.Should().BeFalse();
        await workspace.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task CloneStartsBeforeMetadataExtractionCompletes()
    {
        // Verifies that the workspace clone is kicked off before ExtractMetadataAsync is awaited,
        // proving the two operations run concurrently rather than sequentially.
        var extractionTcs = new TaskCompletionSource<LanguageMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cloneStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var detector = Substitute.For<ILanguageDetector>();
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        detector.ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => extractionTcs.Task);

        var workspace = Substitute.For<IWorkspace>();
        workspace.LocalPath.Returns("/tmp/ws");
        workspace.DisposeAsync().Returns(ValueTask.CompletedTask);

        var workspaceFactory = Substitute.For<IWorkspaceFactory>();
        workspaceFactory.CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cloneStarted.SetResult();
                return workspace;
            });

        var runner = Substitute.For<IBuildRunner>();
        runner.LanguageId.Returns("dotnet");
        runner.RunAsync(Arg.Any<string>(), Arg.Any<GroundingConfig>(), Arg.Any<CancellationToken>())
            .Returns(new BuildResult(true, 0, 0, "ok"));

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["MyApp.csproj"]);

        var buildRequest = Request with { Config = GroundingConfig.Default with { Build = true } };
        var provider = CreateProviderWithBuild([detector], [runner], workspaceFactory, reader);

        var ctxTask = provider.GetContextAsync(buildRequest, CancellationToken.None);

        // Clone factory should be called before metadata extraction unblocks.
        await cloneStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Now unblock metadata extraction.
        extractionTcs.SetResult(new LanguageMetadata("dotnet", "10.0", null, []));

        var ctx = await ctxTask;

        ctx.Language.Should().NotBeNull();
        ctx.Build.Should().NotBeNull();
        ctx.Build!.Success.Should().BeTrue();
    }

    [Fact]
    public async Task CloneFailureDuringParallelExtractionMetadataStillReturnedBuildNull()
    {
        var detector = Substitute.For<ILanguageDetector>();
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        detector.ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LanguageMetadata("dotnet", "10.0", null, []));

        var workspaceFactory = Substitute.For<IWorkspaceFactory>();
        workspaceFactory.CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("remote: Repository not found."));

        var runner = Substitute.For<IBuildRunner>();
        runner.LanguageId.Returns("dotnet");

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["MyApp.csproj"]);

        var buildRequest = Request with { Config = GroundingConfig.Default with { Build = true } };
        var provider = CreateProviderWithBuild([detector], [runner], workspaceFactory, reader);

        var ctx = await provider.GetContextAsync(buildRequest, CancellationToken.None);

        ctx.Language.Should().NotBeNull();
        ctx.Build.Should().BeNull();
        await runner.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<GroundingConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MetadataExtractionThrowsDisposesPreStartedWorkspace()
    {
        var workspace = Substitute.For<IWorkspace>();
        workspace.LocalPath.Returns("/tmp/ws");
        workspace.DisposeAsync().Returns(ValueTask.CompletedTask);

        var workspaceFactory = Substitute.For<IWorkspaceFactory>();
        workspaceFactory.CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(workspace);

        var detector = Substitute.For<ILanguageDetector>();
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        detector.ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("contents API 503"));

        var runner = Substitute.For<IBuildRunner>();
        runner.LanguageId.Returns("dotnet");

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["MyApp.csproj"]);

        var buildRequest = Request with { Config = GroundingConfig.Default with { Build = true } };
        var provider = CreateProviderWithBuild([detector], [runner], workspaceFactory, reader);

        var ctx = await provider.GetContextAsync(buildRequest, CancellationToken.None);

        // Outer catch returns Empty; the pre-started workspace must have been disposed.
        ctx.Language.Should().BeNull();
        ctx.Build.Should().BeNull();
        await workspace.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task BuildDisabledWorkspaceFactoryNeverCalledEvenWhenDetectorMatches()
    {
        var detector = Substitute.For<ILanguageDetector>();
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        detector.ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LanguageMetadata("dotnet", "10.0", null, []));

        var workspaceFactory = Substitute.For<IWorkspaceFactory>();

        var runner = Substitute.For<IBuildRunner>();
        runner.LanguageId.Returns("dotnet");

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["MyApp.csproj"]);

        // Build = false (default GroundingConfig)
        var provider = CreateProviderWithBuild([detector], [runner], workspaceFactory, reader);

        var ctx = await provider.GetContextAsync(Request, CancellationToken.None);

        ctx.Language.Should().NotBeNull();
        ctx.Build.Should().BeNull();
        await workspaceFactory.DidNotReceive().CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetContextAsyncFetchesGitHubChecksWhenTestsEnabled()
    {
        var checkFetcher = Substitute.For<ICheckRunFetcher>();
        var checks = new TestResult(1, 0, 0, "- check build: success", "github_checks");
        checkFetcher.GetHeadCheckSummaryAsync("acme", "myapp", "abc1234", "ghs_token", Arg.Any<CancellationToken>())
            .Returns(checks);

        var detector = Substitute.For<ILanguageDetector>();
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        detector.ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LanguageMetadata("dotnet", "10.0", null, []));

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["MyApp.csproj"]);

        var request = Request with { Config = GroundingConfig.Default with { Tests = true } };
        var provider = CreateProviderWithTests([detector], [], [], null, checkFetcher, reader);

        var ctx = await provider.GetContextAsync(request, CancellationToken.None);

        ctx.Tests.Should().BeSameAs(checks);
        await checkFetcher.Received(1)
            .GetHeadCheckSummaryAsync("acme", "myapp", "abc1234", "ghs_token", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetContextAsyncReturnsChecksWhenNoDetectorMatches()
    {
        var checkFetcher = Substitute.For<ICheckRunFetcher>();
        var checks = new TestResult(0, 1, 0, "- check tests: failure", "github_checks");
        checkFetcher.GetHeadCheckSummaryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(checks);

        var detector = Substitute.For<ILanguageDetector>();
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(false);

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["unknown.xyz"]);

        var request = Request with { Config = GroundingConfig.Default with { Tests = true } };
        var provider = CreateProviderWithTests([detector], [], [], null, checkFetcher, reader);

        var ctx = await provider.GetContextAsync(request, CancellationToken.None);

        ctx.Language.Should().BeNull();
        ctx.Build.Should().BeNull();
        ctx.Tests.Should().BeSameAs(checks);
    }

    [Fact]
    public async Task GetContextAsyncDoesNotFetchChecksWhenTestsDisabled()
    {
        var checkFetcher = Substitute.For<ICheckRunFetcher>();
        var detector = Substitute.For<ILanguageDetector>();
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(false);

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["unknown.xyz"]);

        var provider = CreateProviderWithTests([detector], [], [], null, checkFetcher, reader);

        var ctx = await provider.GetContextAsync(Request, CancellationToken.None);

        ctx.Tests.Should().BeNull();
        await checkFetcher.DidNotReceive()
            .GetHeadCheckSummaryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetContextAsyncDoesNotRunLocalTestsWhenChecksExistAndLocalTestsDisabled()
    {
        var checkFetcher = Substitute.For<ICheckRunFetcher>();
        var checks = new TestResult(1, 0, 0, "- check build: success", "github_checks");
        checkFetcher.GetHeadCheckSummaryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(checks);

        var detector = Substitute.For<ILanguageDetector>();
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        detector.ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LanguageMetadata("dotnet", "10.0", null, []));

        var buildRunner = Substitute.For<IBuildRunner>();
        buildRunner.LanguageId.Returns("dotnet");
        buildRunner.RunAsync(Arg.Any<string>(), Arg.Any<GroundingConfig>(), Arg.Any<CancellationToken>())
            .Returns(new BuildResult(true, 0, 0, "ok"));

        var testRunner = Substitute.For<ITestRunner>();
        testRunner.LanguageId.Returns("dotnet");

        var workspace = Substitute.For<IWorkspace>();
        workspace.LocalPath.Returns("/tmp/ws");
        workspace.DisposeAsync().Returns(ValueTask.CompletedTask);

        var workspaceFactory = Substitute.For<IWorkspaceFactory>();
        workspaceFactory.CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(workspace);

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["MyApp.csproj"]);

        var request = Request with { Config = GroundingConfig.Default with { Tests = true, Build = true } };
        var provider = CreateProviderWithTests([detector], [buildRunner], [testRunner], workspaceFactory, checkFetcher, reader);

        var ctx = await provider.GetContextAsync(request, CancellationToken.None);

        ctx.Tests.Should().BeSameAs(checks);
        await testRunner.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<GroundingConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetContextAsyncRunsLocalTestsAfterSuccessfulBuildWhenEnabled()
    {
        var checkFetcher = Substitute.For<ICheckRunFetcher>();
        var checks = new TestResult(1, 0, 0, "- check build: success", "github_checks");
        checkFetcher.GetHeadCheckSummaryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(checks);

        var detector = Substitute.For<ILanguageDetector>();
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        detector.ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LanguageMetadata("dotnet", "10.0", null, []));

        var buildRunner = Substitute.For<IBuildRunner>();
        buildRunner.LanguageId.Returns("dotnet");
        buildRunner.RunAsync(Arg.Any<string>(), Arg.Any<GroundingConfig>(), Arg.Any<CancellationToken>())
            .Returns(new BuildResult(true, 0, 0, "ok"));

        var testRunner = Substitute.For<ITestRunner>();
        testRunner.LanguageId.Returns("dotnet");
        testRunner.RunAsync("/tmp/ws", Arg.Any<GroundingConfig>(), Arg.Any<CancellationToken>())
            .Returns(new TestResult(12, 0, 1, "local tests ok"));

        var workspace = Substitute.For<IWorkspace>();
        workspace.LocalPath.Returns("/tmp/ws");
        workspace.DisposeAsync().Returns(ValueTask.CompletedTask);

        var workspaceFactory = Substitute.For<IWorkspaceFactory>();
        workspaceFactory.CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(workspace);

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["MyApp.csproj"]);

        var request = Request with { Config = GroundingConfig.Default with { Tests = true, LocalTests = true, Build = true } };
        var provider = CreateProviderWithTests([detector], [buildRunner], [testRunner], workspaceFactory, checkFetcher, reader);

        var ctx = await provider.GetContextAsync(request, CancellationToken.None);

        ctx.Tests.Should().NotBeNull();
        ctx.Tests!.Passed.Should().Be(12);
        ctx.Tests.Output.Should().Contain("local tests ok");
        ctx.Tests.Output.Should().Contain("GitHub Checks:");
        await testRunner.Received(1).RunAsync("/tmp/ws", Arg.Any<GroundingConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetContextAsyncSkipsLocalTestsWhenBuildFails()
    {
        var detector = Substitute.For<ILanguageDetector>();
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        detector.ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LanguageMetadata("dotnet", "10.0", null, []));

        var buildRunner = Substitute.For<IBuildRunner>();
        buildRunner.LanguageId.Returns("dotnet");
        buildRunner.RunAsync(Arg.Any<string>(), Arg.Any<GroundingConfig>(), Arg.Any<CancellationToken>())
            .Returns(new BuildResult(false, 0, 2, "build failed"));

        var testRunner = Substitute.For<ITestRunner>();
        testRunner.LanguageId.Returns("dotnet");

        var workspace = Substitute.For<IWorkspace>();
        workspace.LocalPath.Returns("/tmp/ws");
        workspace.DisposeAsync().Returns(ValueTask.CompletedTask);

        var workspaceFactory = Substitute.For<IWorkspaceFactory>();
        workspaceFactory.CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(workspace);

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["MyApp.csproj"]);

        var request = Request with { Config = GroundingConfig.Default with { LocalTests = true, Build = true } };
        var provider = CreateProviderWithTests([detector], [buildRunner], [testRunner], workspaceFactory, null, reader);

        var ctx = await provider.GetContextAsync(request, CancellationToken.None);

        ctx.Build!.Success.Should().BeFalse();
        ctx.Tests.Should().BeNull();
        await testRunner.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<GroundingConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetContextAsyncTurnsTestRunnerExceptionIntoFailedTestResult()
    {
        var detector = Substitute.For<ILanguageDetector>();
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        detector.ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LanguageMetadata("dotnet", "10.0", null, []));

        var buildRunner = Substitute.For<IBuildRunner>();
        buildRunner.LanguageId.Returns("dotnet");
        buildRunner.RunAsync(Arg.Any<string>(), Arg.Any<GroundingConfig>(), Arg.Any<CancellationToken>())
            .Returns(new BuildResult(true, 0, 0, "ok"));

        var testRunner = Substitute.For<ITestRunner>();
        testRunner.LanguageId.Returns("dotnet");
        testRunner.RunAsync(Arg.Any<string>(), Arg.Any<GroundingConfig>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("test runner exploded"));

        var workspace = Substitute.For<IWorkspace>();
        workspace.LocalPath.Returns("/tmp/ws");
        workspace.DisposeAsync().Returns(ValueTask.CompletedTask);

        var workspaceFactory = Substitute.For<IWorkspaceFactory>();
        workspaceFactory.CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(workspace);

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["MyApp.csproj"]);

        var request = Request with { Config = GroundingConfig.Default with { LocalTests = true, Build = true } };
        var provider = CreateProviderWithTests([detector], [buildRunner], [testRunner], workspaceFactory, null, reader);

        var ctx = await provider.GetContextAsync(request, CancellationToken.None);

        ctx.Tests.Should().Be(new TestResult(0, 0, 0, "test runner exploded"));
    }

    [Fact]
    public async Task GetContextAsyncEmitsGroundingTierSpans()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ReviewBotActivitySource.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (activities)
                {
                    activities.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        var checkFetcher = Substitute.For<ICheckRunFetcher>();
        checkFetcher.GetHeadCheckSummaryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TestResult(1, 0, 0, "checks ok", "github_checks"));

        var detector = Substitute.For<ILanguageDetector>();
        detector.LanguageId.Returns("dotnet");
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        detector.ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LanguageMetadata("dotnet", "10.0", null, []));

        var buildRunner = Substitute.For<IBuildRunner>();
        buildRunner.LanguageId.Returns("dotnet");
        buildRunner.RunAsync(Arg.Any<string>(), Arg.Any<GroundingConfig>(), Arg.Any<CancellationToken>())
            .Returns(new BuildResult(true, 0, 0, "build ok"));

        var testRunner = Substitute.For<ITestRunner>();
        testRunner.LanguageId.Returns("dotnet");
        testRunner.RunAsync(Arg.Any<string>(), Arg.Any<GroundingConfig>(), Arg.Any<CancellationToken>())
            .Returns(new TestResult(12, 1, 0, "local tests ran"));

        var workspace = Substitute.For<IWorkspace>();
        workspace.LocalPath.Returns("/tmp/ws");
        workspace.DisposeAsync().Returns(ValueTask.CompletedTask);

        var workspaceFactory = Substitute.For<IWorkspaceFactory>();
        workspaceFactory.CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(workspace);

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["MyApp.csproj"]);

        var request = Request with { Config = GroundingConfig.Default with { Tests = true, LocalTests = true, Build = true } };
        var provider = CreateProviderWithTests([detector], [buildRunner], [testRunner], workspaceFactory, checkFetcher, reader);

        await provider.GetContextAsync(request, CancellationToken.None);

        List<Activity> snapshot;
        lock (activities)
        {
            snapshot = [..activities];
        }

        var languageActivity = snapshot.Should()
            .ContainSingle(activity => activity.OperationName == "reviewbot.grounding.tier1_language")
            .Subject;
        languageActivity.GetTagItem("grounding.detector").Should().Be("dotnet");
        languageActivity.GetTagItem("grounding.language_id").Should().Be("dotnet");
        languageActivity.GetTagItem("grounding.language_detected").Should().Be(true);

        var buildActivity = snapshot.Should()
            .ContainSingle(activity => activity.OperationName == "reviewbot.grounding.tier2_build")
            .Subject;
        buildActivity.GetTagItem("grounding.language_id").Should().Be("dotnet");
        buildActivity.GetTagItem("grounding.build_ran").Should().Be(true);
        buildActivity.GetTagItem("grounding.build_success").Should().Be(true);

        var testActivities = snapshot
            .Where(activity => activity.OperationName == "reviewbot.grounding.tier3_tests")
            .ToArray();
        testActivities.Should().HaveCount(2);
        testActivities.Should().Contain(activity => Equals(activity.GetTagItem("grounding.test_source"), "github_checks"));
        var localTestActivity = testActivities.Should()
            .ContainSingle(activity => Equals(activity.GetTagItem("grounding.test_source"), "local"))
            .Subject;
        localTestActivity.GetTagItem("grounding.language_id").Should().Be("dotnet");
        localTestActivity.GetTagItem("grounding.tests_found").Should().Be(true);
        localTestActivity.GetTagItem("grounding.tests_failed").Should().Be(1);
    }

    private static CompositeGroundingProvider CreateProvider(
        IReadOnlyList<ILanguageDetector> detectors,
        IRepoContentReader reader) =>
        new(detectors, reader, NullLogger<CompositeGroundingProvider>.Instance);

    private static CompositeGroundingProvider CreateProviderWithBuild(
        IReadOnlyList<ILanguageDetector> detectors,
        IReadOnlyList<IBuildRunner> buildRunners,
        IWorkspaceFactory workspaceFactory,
        IRepoContentReader reader) =>
        new(detectors, buildRunners, workspaceFactory, reader, NullLogger<CompositeGroundingProvider>.Instance);

    private static CompositeGroundingProvider CreateProviderWithTests(
        IReadOnlyList<ILanguageDetector> detectors,
        IReadOnlyList<IBuildRunner> buildRunners,
        IReadOnlyList<ITestRunner> testRunners,
        IWorkspaceFactory? workspaceFactory,
        ICheckRunFetcher? checkRunFetcher,
        IRepoContentReader reader) =>
        new(
            detectors,
            buildRunners,
            testRunners,
            workspaceFactory,
            checkRunFetcher,
            reader,
            NullLogger<CompositeGroundingProvider>.Instance);

    private sealed class StubLanguageDetector : ILanguageDetector
    {
        public string LanguageId => "stub";
        public bool CanDetect(IReadOnlyList<string> rootFileNames) => false;
        public Task<LanguageMetadata?> ExtractMetadataAsync(IRepoContentReader reader, string headSha, CancellationToken ct) =>
            Task.FromResult<LanguageMetadata?>(null);
    }
}
