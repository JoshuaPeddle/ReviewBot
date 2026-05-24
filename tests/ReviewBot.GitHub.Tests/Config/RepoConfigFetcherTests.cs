using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Octokit;
using ReviewBot.Core.Domain;
using ReviewBot.GitHub.Config;
using ReviewBot.GitHub.Pulls;

namespace ReviewBot.GitHub.Tests.Config;

public class RepoConfigFetcherTests
{
    [Fact]
    public async Task FetchAsyncReturnsDefaultWhenConfigFileIsMissing()
    {
        var contents = Substitute.For<IRepositoryContentsClient>();
        contents
            .GetAllContentsByRef("octo", "repo", Arg.Any<string>(), "head-sha")
            .Returns(_ => Task.FromException<IReadOnlyList<RepositoryContent>>(CreateNotFound()));
        var logger = new CapturingLogger<RepoConfigFetcher>();
        var fetcher = CreateFetcher(contents, logger);

        var config = await fetcher.FetchAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        config.Should().BeEquivalentTo(ReviewConfig.Default);
        await contents.Received(1).GetAllContentsByRef("octo", "repo", ".github/review-bot.yml", "head-sha");
        await contents.Received(1).GetAllContentsByRef("octo", "repo", ".github/review-bot.yaml", "head-sha");
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Information);
    }

    [Fact]
    public async Task FetchAsyncMapsValidYaml()
    {
        const string yaml = """
            enabled: false
            model:
              provider: openai
              name: gpt-4.1
              base_url_env_var: REVIEWBOT__OPENAI__BASE_URL
            review:
              inline_comments: false
              summary: true
              max_files: 12
              max_patch_lines: 345
              agentic_context: true
              max_context_requests: 3
              max_context_file_bytes: 12345
              request_changes_on_error: true
              approve_if_clean: true
              full_file_max_bytes: 6789
              trigger:
                on_review_request: false
                on_push: true
            ignore:
              - docs/**
              - generated/**
            focus:
              - performance
              - maintainability
            instructions: "  Keep comments short.  "
            """;
        var contents = Substitute.For<IRepositoryContentsClient>();
        contents
            .GetAllContentsByRef("octo", "repo", ".github/review-bot.yml", "head-sha")
            .Returns([CreateContent(yaml)]);
        var fetcher = CreateFetcher(contents);

        var config = await fetcher.FetchAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        config.Enabled.Should().BeFalse();
        config.Model.Should().Be(new ModelConfig("openai", "gpt-4.1", "REVIEWBOT__OPENAI__BASE_URL"));
        config.Review.Should().Be(new ReviewOutputConfig(
            InlineComments: false,
            Summary: true,
            MaxFiles: 12,
            MaxPatchLines: 345,
            Trigger: new TriggerConfig(OnReviewRequest: false, OnPush: true),
            AgenticContext: true,
            MaxContextRequests: 3,
            MaxContextFileBytes: 12345,
            RequestChangesOnError: true,
            ApproveIfClean: true,
            FullFileMaxBytes: 6789));
        config.Review.AgenticContext.Should().BeTrue();
        config.Review.MaxContextRequests.Should().Be(3);
        config.Review.MaxContextFileBytes.Should().Be(12345);
        config.Ignore.Should().Equal("docs/**", "generated/**");
        config.Focus.Should().Equal("performance", "maintainability");
        config.Instructions.Should().Be("Keep comments short.");
    }

    [Fact]
    public async Task FetchAsyncFallsBackToYamlExtensionWhenYmlIsMissing()
    {
        const string yaml = """
            review:
              max_files: 7
            """;
        var contents = Substitute.For<IRepositoryContentsClient>();
        contents
            .GetAllContentsByRef("octo", "repo", ".github/review-bot.yml", "head-sha")
            .Returns(_ => Task.FromException<IReadOnlyList<RepositoryContent>>(CreateNotFound()));
        contents
            .GetAllContentsByRef("octo", "repo", ".github/review-bot.yaml", "head-sha")
            .Returns([CreateContent(yaml)]);
        var fetcher = CreateFetcher(contents);

        var config = await fetcher.FetchAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        config.Review.MaxFiles.Should().Be(7);
    }

    [Fact]
    public async Task FetchAsyncMergesPartialYamlWithDefaults()
    {
        const string yaml = """
            model:
              name: claude-sonnet-4-5
            """;
        var contents = Substitute.For<IRepositoryContentsClient>();
        contents
            .GetAllContentsByRef("octo", "repo", ".github/review-bot.yml", "head-sha")
            .Returns([CreateContent(yaml)]);
        var fetcher = CreateFetcher(contents);

        var config = await fetcher.FetchAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        config.Model.Should().Be(ReviewConfig.Default.Model with { Name = "claude-sonnet-4-5" });
        config.Enabled.Should().Be(ReviewConfig.Default.Enabled);
        config.Review.Should().Be(ReviewConfig.Default.Review);
        config.Ignore.Should().Equal(ReviewConfig.Default.Ignore);
        config.Focus.Should().Equal(ReviewConfig.Default.Focus);
        config.Instructions.Should().Be(ReviewConfig.Default.Instructions);
    }

    [Fact]
    public async Task FetchAsyncReturnsDefaultAndLogsWarningWhenYamlIsInvalid()
    {
        var contents = Substitute.For<IRepositoryContentsClient>();
        contents
            .GetAllContentsByRef("octo", "repo", ".github/review-bot.yml", "head-sha")
            .Returns([CreateContent("model: [")]);
        var logger = new CapturingLogger<RepoConfigFetcher>();
        var fetcher = CreateFetcher(contents, logger);

        var config = await fetcher.FetchAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        config.Should().BeEquivalentTo(ReviewConfig.Default);
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task FetchAsyncFallsBackToDefaultProviderWhenProviderIsUnknown()
    {
        const string yaml = """
            model:
              provider: local
              name: custom-model
            """;
        var contents = Substitute.For<IRepositoryContentsClient>();
        contents
            .GetAllContentsByRef("octo", "repo", ".github/review-bot.yml", "head-sha")
            .Returns([CreateContent(yaml)]);
        var logger = new CapturingLogger<RepoConfigFetcher>();
        var fetcher = CreateFetcher(contents, logger);

        var config = await fetcher.FetchAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        config.Model.Should().Be(ReviewConfig.Default.Model with { Name = "custom-model" });
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("Unknown ReviewBot model provider", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchAsyncMapsGroundingSection()
    {
        const string yaml = """
            grounding:
              enabled: false
              build: true
              tests: true
              local_tests: true
              build_timeout_seconds: 60
              test_timeout_seconds: 180
              build_command: "dotnet build -c Release"
              test_command: "dotnet test"
            """;
        var contents = Substitute.For<IRepositoryContentsClient>();
        contents
            .GetAllContentsByRef("octo", "repo", ".github/review-bot.yml", "head-sha")
            .Returns([CreateContent(yaml)]);
        var fetcher = CreateFetcher(contents);

        var config = await fetcher.FetchAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        config.Grounding.Enabled.Should().BeFalse();
        config.Grounding.Build.Should().BeTrue();
        config.Grounding.Tests.Should().BeTrue();
        config.Grounding.LocalTests.Should().BeTrue();
        config.Grounding.BuildTimeoutSeconds.Should().Be(60);
        config.Grounding.TestTimeoutSeconds.Should().Be(180);
        config.Grounding.BuildCommand.Should().Be("dotnet build -c Release");
        config.Grounding.TestCommand.Should().Be("dotnet test");
    }

    [Fact]
    public async Task FetchAsyncMergesPartialGroundingWithDefaults()
    {
        const string yaml = """
            grounding:
              build: true
            """;
        var contents = Substitute.For<IRepositoryContentsClient>();
        contents
            .GetAllContentsByRef("octo", "repo", ".github/review-bot.yml", "head-sha")
            .Returns([CreateContent(yaml)]);
        var fetcher = CreateFetcher(contents);

        var config = await fetcher.FetchAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        config.Grounding.Build.Should().BeTrue();
        config.Grounding.Enabled.Should().Be(GroundingConfig.Default.Enabled);
        config.Grounding.Tests.Should().Be(GroundingConfig.Default.Tests);
        config.Grounding.LocalTests.Should().Be(GroundingConfig.Default.LocalTests);
        config.Grounding.BuildTimeoutSeconds.Should().Be(GroundingConfig.Default.BuildTimeoutSeconds);
        config.Grounding.TestTimeoutSeconds.Should().Be(GroundingConfig.Default.TestTimeoutSeconds);
        config.Grounding.BuildCommand.Should().Be(GroundingConfig.Default.BuildCommand);
        config.Grounding.TestCommand.Should().Be(GroundingConfig.Default.TestCommand);
    }

    [Fact]
    public async Task FetchAsyncLocalTestsImpliesTests()
    {
        const string yaml = """
            grounding:
              local_tests: true
            """;
        var contents = Substitute.For<IRepositoryContentsClient>();
        contents
            .GetAllContentsByRef("octo", "repo", ".github/review-bot.yml", "head-sha")
            .Returns([CreateContent(yaml)]);
        var fetcher = CreateFetcher(contents);

        var config = await fetcher.FetchAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        config.Grounding.LocalTests.Should().BeTrue();
        config.Grounding.Tests.Should().BeTrue();
    }

    [Fact]
    public async Task FetchAsyncMapsMinConfidenceHigh()
    {
        const string yaml = """
            review:
              min_confidence: high
            """;
        var contents = Substitute.For<IRepositoryContentsClient>();
        contents
            .GetAllContentsByRef("octo", "repo", ".github/review-bot.yml", "head-sha")
            .Returns([CreateContent(yaml)]);
        var fetcher = CreateFetcher(contents);

        var config = await fetcher.FetchAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        config.Review.MinConfidence.Should().Be(Confidence.High);
    }

    [Fact]
    public async Task FetchAsyncMapsMinConfidenceMedium()
    {
        const string yaml = """
            review:
              min_confidence: medium
            """;
        var contents = Substitute.For<IRepositoryContentsClient>();
        contents
            .GetAllContentsByRef("octo", "repo", ".github/review-bot.yml", "head-sha")
            .Returns([CreateContent(yaml)]);
        var fetcher = CreateFetcher(contents);

        var config = await fetcher.FetchAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        config.Review.MinConfidence.Should().Be(Confidence.Medium);
    }

    [Fact]
    public async Task FetchAsyncDefaultsMinConfidenceToLowWhenMissing()
    {
        const string yaml = """
            review:
              max_files: 10
            """;
        var contents = Substitute.For<IRepositoryContentsClient>();
        contents
            .GetAllContentsByRef("octo", "repo", ".github/review-bot.yml", "head-sha")
            .Returns([CreateContent(yaml)]);
        var fetcher = CreateFetcher(contents);

        var config = await fetcher.FetchAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        config.Review.MinConfidence.Should().Be(Confidence.Low);
    }

    [Fact]
    public async Task FetchAsyncMapsSelfCritique()
    {
        const string yaml = """
            review:
              self_critique: true
            """;
        var contents = Substitute.For<IRepositoryContentsClient>();
        contents
            .GetAllContentsByRef("octo", "repo", ".github/review-bot.yml", "head-sha")
            .Returns([CreateContent(yaml)]);
        var fetcher = CreateFetcher(contents);

        var config = await fetcher.FetchAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        config.Review.SelfCritique.Should().BeTrue();
    }

    [Fact]
    public async Task FetchAsyncMapsAgenticContextLimits()
    {
        const string yaml = """
            review:
              agentic_context: true
              max_context_requests: 4
              max_context_file_bytes: 64000
            """;
        var contents = Substitute.For<IRepositoryContentsClient>();
        contents
            .GetAllContentsByRef("octo", "repo", ".github/review-bot.yml", "head-sha")
            .Returns([CreateContent(yaml)]);
        var fetcher = CreateFetcher(contents);

        var config = await fetcher.FetchAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        config.Review.AgenticContext.Should().BeTrue();
        config.Review.MaxContextRequests.Should().Be(4);
        config.Review.MaxContextFileBytes.Should().Be(64_000);
    }

    [Fact]
    public async Task FetchAsyncMapsReviewStateFlags()
    {
        const string yaml = """
            review:
              request_changes_on_error: true
              approve_if_clean: true
            """;
        var contents = Substitute.For<IRepositoryContentsClient>();
        contents
            .GetAllContentsByRef("octo", "repo", ".github/review-bot.yml", "head-sha")
            .Returns([CreateContent(yaml)]);
        var fetcher = CreateFetcher(contents);

        var config = await fetcher.FetchAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        config.Review.RequestChangesOnError.Should().BeTrue();
        config.Review.ApproveIfClean.Should().BeTrue();
    }

    [Fact]
    public async Task FetchAsyncMapsFullFileMaxBytesAndAllowsZero()
    {
        const string yaml = """
            review:
              full_file_max_bytes: 0
            """;
        var contents = Substitute.For<IRepositoryContentsClient>();
        contents
            .GetAllContentsByRef("octo", "repo", ".github/review-bot.yml", "head-sha")
            .Returns([CreateContent(yaml)]);
        var logger = new CapturingLogger<RepoConfigFetcher>();
        var fetcher = CreateFetcher(contents, logger);

        var config = await fetcher.FetchAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        config.Review.FullFileMaxBytes.Should().Be(0);
        logger.Entries.Should().NotContain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("review.full_file_max_bytes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchAsyncLogsWarningAndDefaultsLowOnUnknownMinConfidence()
    {
        const string yaml = """
            review:
              min_confidence: extreme
            """;
        var contents = Substitute.For<IRepositoryContentsClient>();
        contents
            .GetAllContentsByRef("octo", "repo", ".github/review-bot.yml", "head-sha")
            .Returns([CreateContent(yaml)]);
        var logger = new CapturingLogger<RepoConfigFetcher>();
        var fetcher = CreateFetcher(contents, logger);

        var config = await fetcher.FetchAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        config.Review.MinConfidence.Should().Be(Confidence.Low);
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("Unknown ReviewBot min_confidence value", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchAsyncFallsBackToDefaultsWhenNumericLimitsAreInvalid()
    {
        const string yaml = """
            review:
              max_files: 0
              max_patch_lines: -1
              max_context_requests: 0
              max_context_file_bytes: -10
              full_file_max_bytes: -20
            """;
        var contents = Substitute.For<IRepositoryContentsClient>();
        contents
            .GetAllContentsByRef("octo", "repo", ".github/review-bot.yml", "head-sha")
            .Returns([CreateContent(yaml)]);
        var logger = new CapturingLogger<RepoConfigFetcher>();
        var fetcher = CreateFetcher(contents, logger);

        var config = await fetcher.FetchAsync("octo", "repo", "head-sha", "ghs_token", CancellationToken.None);

        config.Review.MaxFiles.Should().Be(ReviewConfig.Default.Review.MaxFiles);
        config.Review.MaxPatchLines.Should().Be(ReviewConfig.Default.Review.MaxPatchLines);
        config.Review.MaxContextRequests.Should().Be(ReviewConfig.Default.Review.MaxContextRequests);
        config.Review.MaxContextFileBytes.Should().Be(ReviewConfig.Default.Review.MaxContextFileBytes);
        config.Review.FullFileMaxBytes.Should().Be(ReviewConfig.Default.Review.FullFileMaxBytes);
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("review.max_files=0", StringComparison.Ordinal));
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("review.max_patch_lines=-1", StringComparison.Ordinal));
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("review.max_context_requests=0", StringComparison.Ordinal));
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("review.max_context_file_bytes=-10", StringComparison.Ordinal));
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("review.full_file_max_bytes=-20", StringComparison.Ordinal));
    }

    private static RepoConfigFetcher CreateFetcher(
        IRepositoryContentsClient contents,
        ILogger<RepoConfigFetcher>? logger = null)
    {
        var repositories = Substitute.For<IRepositoriesClient>();
        repositories.Content.Returns(contents);

        var client = Substitute.For<IGitHubClient>();
        client.Repository.Returns(repositories);

        var clientFactory = Substitute.For<IGitHubClientFactory>();
        clientFactory.CreateForInstallation("ghs_token").Returns(client);

        return new RepoConfigFetcher(clientFactory, logger ?? new CapturingLogger<RepoConfigFetcher>());
    }

    private static RepositoryContent CreateContent(string text) => new(
        "review-bot.yml",
        ".github/review-bot.yml",
        "blob-sha",
        text.Length,
        ContentType.File,
        null!,
        "https://api.github.com/repos/octo/repo/contents/.github/review-bot.yml",
        "https://api.github.com/repos/octo/repo/git/blobs/blob-sha",
        "https://github.com/octo/repo/blob/head-sha/.github/review-bot.yml",
        "base64",
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
        null!,
        null!);

    private static NotFoundException CreateNotFound() => new("Not found", HttpStatusCode.NotFound);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
