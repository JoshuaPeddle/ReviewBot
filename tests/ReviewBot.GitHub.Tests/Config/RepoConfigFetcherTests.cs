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
            Trigger: new TriggerConfig(OnReviewRequest: false, OnPush: true)));
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
