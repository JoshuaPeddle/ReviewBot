using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ReviewBot.Core.Domain;
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
    public async Task GetContextAsyncReturnsBuildFailureWhenWorkspaceCreationFails()
    {
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
        ctx.Build.Should().NotBeNull();
        ctx.Build!.Success.Should().BeFalse();
        ctx.Build.Output.Should().Contain("git fetch failed");
    }

    [Fact]
    public async Task GetContextAsyncReturnsBuildNullWhenNoRunnerMatchesLanguage()
    {
        var detector = Substitute.For<ILanguageDetector>();
        detector.CanDetect(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        detector.ExtractMetadataAsync(Arg.Any<IRepoContentReader>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LanguageMetadata("go", "1.23", null, []));

        var runner = Substitute.For<IBuildRunner>();
        runner.LanguageId.Returns("dotnet");

        var workspaceFactory = Substitute.For<IWorkspaceFactory>();

        var reader = Substitute.For<IRepoContentReader>();
        reader.ListRootFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["go.mod"]);

        var buildRequest = Request with { Config = GroundingConfig.Default with { Build = true } };
        var provider = CreateProviderWithBuild([detector], [runner], workspaceFactory, reader);

        var ctx = await provider.GetContextAsync(buildRequest, CancellationToken.None);

        ctx.Language.Should().NotBeNull();
        ctx.Build.Should().BeNull();
        await workspaceFactory.DidNotReceive().CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>());
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

    private sealed class StubLanguageDetector : ILanguageDetector
    {
        public string LanguageId => "stub";
        public bool CanDetect(IReadOnlyList<string> rootFileNames) => false;
        public Task<LanguageMetadata?> ExtractMetadataAsync(IRepoContentReader reader, string headSha, CancellationToken ct) =>
            Task.FromResult<LanguageMetadata?>(null);
    }
}
