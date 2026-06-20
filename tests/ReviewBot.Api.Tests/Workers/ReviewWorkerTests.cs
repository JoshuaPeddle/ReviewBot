using System.Diagnostics;
using System.Diagnostics.Metrics;
using DiagActivity = System.Diagnostics.Activity;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Octokit;
using ReviewBot.Api.Cost;
using ReviewBot.Api.Tracing;
using ReviewBot.Core.Context;
using ReviewBot.Api.Workers;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Jobs;
using ReviewBot.Core.Llm;
using ReviewBot.Core.Otel;
using ReviewBot.Core.Prompting;
using ReviewBot.Core.Storage;
using ReviewBot.GitHub.Auth;
using ReviewBot.GitHub.Config;
using ReviewBot.GitHub.Pulls;
using ReviewBot.Grounding;
using ReviewBot.Grounding.Workspace;
using ReviewBot.Retrieval;
using ReviewBot.Retrieval.Indexing;

namespace ReviewBot.Api.Tests.Workers;

public class ReviewWorkerTests
{
    [Fact]
    public async Task ProcessesOneJobThroughTheReviewPipeline()
    {
        await using var fixture = new WorkerFixture();
        var files = new[] { CreateFile("src/B.cs"), CreateFile("src/A.cs") };
        var result = new ReviewResult(
            "Found one issue.",
            [new InlineComment("src/A.cs", 2, "RIGHT", "This should handle null input before dereferencing the value.", Severity.Warning)]);
        ReviewRequest? capturedRequest = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync("octo-org", "reviewbot", "event-head", "install-token", Arg.Any<CancellationToken>())
            .Returns(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchMetadataAsync("octo-org", "reviewbot", 42, "install-token", Arg.Any<CancellationToken>())
            .Returns(CreateMetadata());
        fixture.PullRequestFetcher.FetchFilesAsync("octo-org", "reviewbot", 42, "install-token", 50, Arg.Any<IReadOnlySet<string>?>(), Arg.Any<CancellationToken>())
            .Returns(files);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedRequest = call.Arg<ReviewRequest>();
                return result;
            });
        fixture.ReviewPoster.PostAsync(
                "octo-org",
                "reviewbot",
                42,
                "snapshot-head",
                Arg.Any<ReviewResult>(),
                Arg.Any<IReadOnlyList<FileChange>>(),
                "install-token",
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        capturedRequest.Should().NotBeNull();
        capturedRequest!.PrTitle.Should().Be("Improve parser");
        capturedRequest.PrBody.Should().Be("Adds coverage.");
        capturedRequest.BaseSha.Should().Be("base-sha");
        capturedRequest.HeadSha.Should().Be("snapshot-head");
        capturedRequest.Files.Select(file => file.Path).Should().Equal("src/B.cs", "src/A.cs");
        capturedRequest.Config.Should().Be(ReviewConfig.Default);
        fixture.LlmFactory.Received(1).Create(ReviewConfig.Default.Model);
    }

    [Fact]
    public async Task DisabledConfigShortCircuitsBeforeFetchingFiles()
    {
        await using var fixture = new WorkerFixture();
        var configFetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disabledConfig = ReviewConfig.Default with { Enabled = false };

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                configFetched.SetResult();
                return disabledConfig;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await configFetched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        await fixture.PullRequestFetcher.DidNotReceiveWithAnyArgs()
            .FetchFilesAsync(default!, default!, default, default!, default, default, default);
        await fixture.GroundingProvider.DidNotReceiveWithAnyArgs()
            .GetContextAsync(default!, default);
        fixture.LlmFactory.DidNotReceiveWithAnyArgs().Create(default!);
        await fixture.ReviewPoster.DidNotReceiveWithAnyArgs()
            .PostAsync(default!, default!, default, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task DisabledConfigLogsSpeculativeMetadataFailures()
    {
        var logger = new CapturingLogger<ReviewWorker>();
        await using var fixture = new WorkerFixture(logger: logger);
        var configFetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new TaskCompletionSource<PullRequestMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                configFetched.SetResult();
                return ReviewConfig.Default with { Enabled = false };
            });
        fixture.PullRequestFetcher.FetchMetadataAsync(default!, default!, default, default!, default)
            .ReturnsForAnyArgs(metadata.Task);

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await configFetched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        metadata.SetException(new InvalidOperationException("metadata failed"));

        var logEntry = await logger.WarningLogged.Task.WaitAsync(TimeSpan.FromSeconds(2));
        logEntry.Message.Should().Contain("PR metadata fetch failed");
        logEntry.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task SynchronizeWithOnPushDisabledShortCircuits()
    {
        await using var fixture = new WorkerFixture();
        var configFetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                configFetched.SetResult();
                return ReviewConfig.Default;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(reason: "synchronize"), CancellationToken.None);

        await configFetched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        await fixture.PullRequestFetcher.DidNotReceiveWithAnyArgs()
            .FetchFilesAsync(default!, default!, default, default!, default, default, default);
        await fixture.GroundingProvider.DidNotReceiveWithAnyArgs()
            .GetContextAsync(default!, default);
        fixture.LlmFactory.DidNotReceiveWithAnyArgs().Create(default!);
    }

    [Fact]
    public async Task EventHeadShaStartsConfigAndMetadataFetchesInParallel()
    {
        await using var fixture = new WorkerFixture();
        var metadataStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(async _ =>
            {
                await metadataStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                return ReviewConfig.Default;
            });
        fixture.PullRequestFetcher.FetchMetadataAsync(default!, default!, default, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                metadataStarted.SetResult();
                return CreateMetadata();
            });
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("Parallel.", []));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task FullFileContextWaitsForGroundingBeforeBudgetedFetch()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { FullFileMaxBytes = 10_000 }
        };
        var groundingCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fileContentFetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.PullRequestFetcher.GetFileContentsAsync(default!, default!, default!, default!, default, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                groundingCompleted.Task.IsCompletedSuccessfully.Should().BeTrue();
                fileContentFetchStarted.SetResult();
                return [("src/App.cs", "public class App { }")];
            });
        fixture.GroundingProvider.GetContextAsync(Arg.Any<GroundingRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                groundingCompleted.SetResult();
                return new GroundingContext(new LanguageMetadata("dotnet", "10.0", null, []), null, null);
            });
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("Parallel context.", []));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await fileContentFetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GroundingFailureStopsBeforeFullFileContextAndLlm()
    {
        var logger = new CapturingLogger<ReviewWorker>();
        await using var fixture = new WorkerFixture(logger: logger);
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { FullFileMaxBytes = 10_000 }
        };
        var groundingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.GroundingProvider.GetContextAsync(Arg.Any<GroundingRequest>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(_ =>
            {
                groundingStarted.SetResult();
                return Task.FromException<GroundingContext>(new InvalidOperationException("grounding failed"));
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await groundingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var logEntry = await logger.ErrorLogged.Task.WaitAsync(TimeSpan.FromSeconds(2));

        logEntry.Message.Should().Contain("failed; continuing with the next job");
        await fixture.PullRequestFetcher.DidNotReceiveWithAnyArgs()
            .GetFileContentsAsync(default!, default!, default!, default!, default, default!, default);
        fixture.LlmFactory.DidNotReceiveWithAnyArgs().Create(default!);
        await fixture.ReviewPoster.DidNotReceiveWithAnyArgs()
            .PostAsync(default!, default!, default, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task IgnoreGlobsDropMatchingFilesBeforeLlmAndPost()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with { Ignore = ["**/*.generated.cs", "docs/**"] };
        var threeFiles = new[]
        {
            CreateFile("src/App.cs"),
            CreateFile("src/App.generated.cs"),
            CreateFile("docs/readme.md")
        };
        ReviewRequest? capturedRequest = null;
        IReadOnlyList<FileChange>? postedFiles = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(threeFiles);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedRequest = call.Arg<ReviewRequest>();
                return new ReviewResult("Filtered.", []);
            });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postedFiles = call.ArgAt<IReadOnlyList<FileChange>>(5);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        capturedRequest!.Files.Select(file => file.Path).Should().Equal("src/App.cs");
        postedFiles!.Select(file => file.Path).Should().Equal("src/App.cs");
    }

    [Fact]
    public async Task MaxFilesTrimsByPathOrderAfterIgnoreGlobs()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { MaxFiles = 2 }
        };
        var threeFiles = new[]
        {
            CreateFile("src/C.cs"),
            CreateFile("src/A.cs"),
            CreateFile("src/B.cs")
        };
        ReviewRequest? capturedRequest = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync("octo-org", "reviewbot", 42, "install-token", 2, Arg.Any<IReadOnlySet<string>?>(), Arg.Any<CancellationToken>())
            .Returns(threeFiles);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedRequest = call.Arg<ReviewRequest>();
                return new ReviewResult("Trimmed.", []);
            });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        capturedRequest!.Files.Select(file => file.Path).Should().Equal("src/A.cs", "src/B.cs");
    }

    [Fact]
    public async Task BigPrPatchBudgetPrioritizesSmallestFilesAndPostsSkippedNote()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { MaxPatchLines = 2, ChunkedReview = false }
        };
        var threeFiles = new[]
        {
            CreateFile("src/Large.cs", patchLines: 8),
            CreateFile("src/Small.cs", patchLines: 3),
            CreateFile("src/Medium.cs", patchLines: 5)
        };
        ReviewRequest? capturedRequest = null;
        ReviewResult? postedResult = null;
        IReadOnlyList<FileChange>? postedFiles = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(threeFiles);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedRequest = call.Arg<ReviewRequest>();
                return new ReviewResult("Reviewed the selected files.", []);
            });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postedResult = call.ArgAt<ReviewResult>(4);
                postedFiles = call.ArgAt<IReadOnlyList<FileChange>>(5);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        capturedRequest!.Files.Select(file => file.Path).Should().Equal("src/Small.cs", "src/Medium.cs");
        postedFiles!.Select(file => file.Path).Should().Equal("src/Small.cs", "src/Medium.cs");
        postedResult!.Summary.Should().Contain("Reviewed the selected files.");
        postedResult.Summary.Should().Contain("files_skipped:");
        postedResult.Summary.Should().Contain("`src/Large.cs`");
    }

    [Fact]
    public async Task BigPrPatchBudgetKeepsAllFilesWhenTotalIsAtHeuristicCeiling()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { MaxPatchLines = 2, ChunkedReview = false }
        };
        var twoFiles = new[]
        {
            CreateFile("src/A.cs", patchLines: 4),
            CreateFile("src/B.cs", patchLines: 6)
        };
        ReviewRequest? capturedRequest = null;
        ReviewResult? postedResult = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(twoFiles);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedRequest = call.Arg<ReviewRequest>();
                return new ReviewResult("Within budget.", []);
            });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postedResult = call.ArgAt<ReviewResult>(4);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        capturedRequest!.Files.Select(file => file.Path).Should().Equal("src/A.cs", "src/B.cs");
        postedResult!.Summary.Should().NotContain("files_skipped:");
    }

    [Fact]
    public async Task OutputConfigDropsInlineCommentsAndSummaryBeforePosting()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with
            {
                InlineComments = false,
                Summary = false
            }
        };
        ReviewResult? postedResult = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Hidden summary.",
                [new InlineComment("src/App.cs", 2, "RIGHT", "Hidden comment.", Severity.Warning)]));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postedResult = call.ArgAt<ReviewResult>(4);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        postedResult!.Summary.Should().BeEmpty();
        postedResult.Comments.Should().BeEmpty();
    }

    [Fact]
    public async Task FullFileContextFetchesOnlySmallNonDeletedFilesAndPassesContentToLlm()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { FullFileMaxBytes = 10_000 }
        };
        var files = new[]
        {
            CreateFile("src/Small.cs"),
            CreateFile("src/Large.cs", patch: new string('x', 10_001), commentableLines: new HashSet<int> { 1 }),
            CreateFile("src/Deleted.cs", "@@ -1 +0 @@\n-old", new HashSet<int> { 1 }, FileChangeStatus.Removed)
        };
        IReadOnlyList<ContextRequest>? fetchedRequests = null;
        ReviewRequest? capturedRequest = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(files);
        fixture.PullRequestFetcher.GetFileContentsAsync(default!, default!, default!, default!, default, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                fetchedRequests = call.ArgAt<IReadOnlyList<ContextRequest>>(2);
                return [("src/Small.cs", "public class Small { }")];
            });
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedRequest = call.Arg<ReviewRequest>();
                return new ReviewResult("Reviewed.", []);
            });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        fetchedRequests!.Select(request => request.Path).Should().Equal("src/Small.cs");
        capturedRequest!.FullFileContents.Should().NotBeNull();
        capturedRequest.FullFileContents.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>("src/Small.cs", "public class Small { }"));
    }

    [Fact]
    public async Task FullFileContextUsesRemainingPromptBudgetToLimitFetchRequests()
    {
        var estimator = new KeywordTokenEstimator(
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["small patch"] = 5,
                ["large patch"] = 50,
                ["public class Small"] = 5
            });
        await using var fixture = new WorkerFixture(
            modelContextRegistry: new FixedModelContextRegistry(20),
            tokenEstimator: estimator);
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with
            {
                FullFileMaxBytes = 10_000,
                ResponseReserveTokens = 0
            }
        };
        var files = new[]
        {
            CreateFile("src/Small.cs", "@@ -1 +1 @@\n+small patch", new HashSet<int> { 1 }),
            CreateFile("src/Large.cs", "@@ -1 +1 @@\n+large patch", new HashSet<int> { 1 })
        };
        IReadOnlyList<ContextRequest>? fetchedRequests = null;
        ReviewRequest? capturedRequest = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(files);
        fixture.PullRequestFetcher.GetFileContentsAsync(default!, default!, default!, default!, default, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                fetchedRequests = call.ArgAt<IReadOnlyList<ContextRequest>>(2);
                return [("src/Small.cs", "public class Small { }")];
            });
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedRequest = call.Arg<ReviewRequest>();
                return new ReviewResult("Reviewed.", []);
            });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        fetchedRequests!.Select(request => request.Path).Should().Equal("src/Small.cs");
        capturedRequest!.FullFileContents.Should().ContainSingle()
            .Which.Key.Should().Be("src/Small.cs");
    }

    [Fact]
    public async Task FullFileContextSkipsMostlyNewFiles()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { FullFileMaxBytes = 10_000 }
        };
        var files = new[]
        {
            CreateFile("src/MostlyNew.cs", additionsCount: 10, deletionsCount: 0),
            CreateFile("src/Mixed.cs", additionsCount: 9, deletionsCount: 1),
            CreateFile("src/Existing.cs", additionsCount: 4, deletionsCount: 2)
        };
        IReadOnlyList<ContextRequest>? fetchedRequests = null;
        ReviewRequest? capturedRequest = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(files);
        fixture.PullRequestFetcher.GetFileContentsAsync(default!, default!, default!, default!, default, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                fetchedRequests = call.ArgAt<IReadOnlyList<ContextRequest>>(2);
                return
                [
                    ("src/Mixed.cs", "public class Mixed { }"),
                    ("src/Existing.cs", "public class Existing { }")
                ];
            });
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedRequest = call.Arg<ReviewRequest>();
                return new ReviewResult("Reviewed.", []);
            });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        fetchedRequests!.Select(request => request.Path).Should().Equal("src/Mixed.cs", "src/Existing.cs");
        capturedRequest!.FullFileContents.Should().ContainKeys("src/Mixed.cs", "src/Existing.cs");
        capturedRequest.FullFileContents.Should().NotContainKey("src/MostlyNew.cs");
    }

    [Fact]
    public async Task ChunkedReviewSplitsOversizedDiffAndPostsMergedComments()
    {
        var estimator = new KeywordTokenEstimator(
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["chunk-token"] = 10
            });
        await using var fixture = new WorkerFixture(
            modelContextRegistry: new FixedModelContextRegistry(20),
            tokenEstimator: estimator);
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with
            {
                ResponseReserveTokens = 0,
                ChunkHeadroom = 0.5,
                MaxChunks = 10,
                SelfCritique = true
            }
        };
        var files = Enumerable.Range(1, 5)
            .Select(i => CreateFile($"src/File{i}.cs", "@@ -1 +1 @@\n+chunk-token", new HashSet<int> { 1 }))
            .ToArray();
        var capturedRequests = new List<ReviewRequest>();
        var selfCritiqueCalls = 0;
        ReviewResult? postedResult = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(files);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<ReviewRequest>();
                capturedRequests.Add(request);
                var file = request.Files.Single();
                return new ReviewResult(
                    $"Reviewed {file.Path}.",
                    [new InlineComment(file.Path, 1, "RIGHT", $"Issue in {file.Path}.", Severity.Warning, Confidence.Medium)]);
            });
        fixture.Llm.CompleteRawAsync(Arg.Any<PromptPayload>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(call =>
            {
                call.ArgAt<string>(2).Should().Be("self_critique");
                Interlocked.Increment(ref selfCritiqueCalls);
                return """{"retained_indices":[0,1,2,3,4],"rationale":"merged chunk comments are valid"}""";
            });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postedResult = call.ArgAt<ReviewResult>(4);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        capturedRequests.Should().HaveCount(5);
        capturedRequests.Select(request => request.Files.Single().Path)
            .Should().Equal("src/File1.cs", "src/File2.cs", "src/File3.cs", "src/File4.cs", "src/File5.cs");
        capturedRequests.Select(request => request.ChunkIndex).Should().Equal(1, 2, 3, 4, 5);
        capturedRequests.Select(request => request.TotalChunks).Should().OnlyContain(total => total == 5);
        postedResult!.Comments.Should().HaveCount(5);
        postedResult.Comments.Select(comment => comment.Path)
            .Should().Equal("src/File1.cs", "src/File2.cs", "src/File3.cs", "src/File4.cs", "src/File5.cs");
        postedResult.Summary.Should().StartWith("Reviewed 5 file(s) across 5 chunk(s). Found 5 actionable issues; highest severity: warning.");
        postedResult.Summary.Should().NotContain("Reviewed src/File1.cs.");
        postedResult.Summary.Should().NotContain("Reviewed src/File2.cs.");
        selfCritiqueCalls.Should().Be(1);
    }

    [Fact]
    public async Task PromptBudgetingUsesProviderAwareTokenEstimatorForConfiguredModel()
    {
        var reviewTokenEstimator = new ProviderKeywordTokenEstimator(
            "anthropic",
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["provider-token"] = 10
            });
        await using var fixture = new WorkerFixture(
            modelContextRegistry: new FixedModelContextRegistry(19),
            reviewTokenEstimator: reviewTokenEstimator);
        var config = ReviewConfig.Default with
        {
            Model = new ModelConfig("anthropic", "claude-test", null),
            Review = ReviewConfig.Default.Review with
            {
                ResponseReserveTokens = 0,
                ChunkHeadroom = 0.5,
                MaxChunks = 10
            }
        };
        var files = new[]
        {
            CreateFile("src/A.cs", "@@ -1 +1 @@\n+provider-token", new HashSet<int> { 1 }),
            CreateFile("src/B.cs", "@@ -1 +1 @@\n+provider-token", new HashSet<int> { 1 })
        };
        var capturedRequests = new List<ReviewRequest>();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(files);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedRequests.Add(call.Arg<ReviewRequest>());
                return new ReviewResult("reviewed", []);
            });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        reviewTokenEstimator.SeenProviders.Should().Contain("anthropic");
        capturedRequests.Should().HaveCount(2);
        capturedRequests.Select(request => request.Files.Single().Path)
            .Should().Equal("src/A.cs", "src/B.cs");
    }

    [Fact]
    public async Task ChunkedReviewSelfCritiqueUsesOnlyReviewedFilesWhenMaxChunksCapsReview()
    {
        var estimator = new KeywordTokenEstimator(
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["chunk-token"] = 10
            });
        await using var fixture = new WorkerFixture(
            modelContextRegistry: new FixedModelContextRegistry(20),
            tokenEstimator: estimator);
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with
            {
                ResponseReserveTokens = 0,
                ChunkHeadroom = 0.5,
                MaxChunks = 2,
                SelfCritique = true
            }
        };
        var files = new[]
        {
            CreateFile("src/A.cs", "@@ -1 +1 @@\n+chunk-token", new HashSet<int> { 1 }),
            CreateFile("src/B.cs", "@@ -1 +1 @@\n+chunk-token", new HashSet<int> { 1 }),
            CreateFile("src/C.cs", "@@ -1 +1 @@\n+chunk-token", new HashSet<int> { 1 })
        };
        PromptPayload? selfCritiquePrompt = null;
        ReviewResult? postedResult = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(files);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var file = call.Arg<ReviewRequest>().Files.Single();
                return new ReviewResult(
                    $"Reviewed {file.Path}.",
                    [new InlineComment(file.Path, 1, "RIGHT", $"Issue in {file.Path}.", Severity.Warning, Confidence.Medium)]);
            });
        fixture.Llm.CompleteRawAsync(Arg.Any<PromptPayload>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(call =>
            {
                call.ArgAt<string>(2).Should().Be("self_critique");
                selfCritiquePrompt = call.Arg<PromptPayload>();
                return """{"retained_indices":[0,1],"rationale":"reviewed chunk comments are valid"}""";
            });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postedResult = call.ArgAt<ReviewResult>(4);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        selfCritiquePrompt.Should().NotBeNull();
        selfCritiquePrompt!.UserPrompt.Should().Contain("=== src/A.cs");
        selfCritiquePrompt.UserPrompt.Should().Contain("=== src/B.cs");
        selfCritiquePrompt.UserPrompt.Should().NotContain("=== src/C.cs");
        postedResult!.Summary.Should().Contain("files_skipped:");
        postedResult.Summary.Should().Contain("`src/C.cs`");
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task ChunkedReviewDispatchesAccordingToLlmParallelSupport(
        bool supportsParallelRequests,
        int expectedMinimumConcurrency)
    {
        var estimator = new KeywordTokenEstimator(
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["chunk-token"] = 10
            });
        await using var fixture = new WorkerFixture(
            modelContextRegistry: new FixedModelContextRegistry(20),
            tokenEstimator: estimator);
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with
            {
                ResponseReserveTokens = 0,
                ChunkHeadroom = 0.5
            }
        };
        var files = new[]
        {
            CreateFile("src/A.cs", "@@ -1 +1 @@\n+chunk-token", new HashSet<int> { 1 }),
            CreateFile("src/B.cs", "@@ -1 +1 @@\n+chunk-token", new HashSet<int> { 1 }),
            CreateFile("src/C.cs", "@@ -1 +1 @@\n+chunk-token", new HashSet<int> { 1 })
        };
        var currentConcurrency = 0;
        var maxConcurrency = 0;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.Llm.SupportsParallelRequests.Returns(supportsParallelRequests);
        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(files);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var active = Interlocked.Increment(ref currentConcurrency);
                maxConcurrency = Math.Max(maxConcurrency, active);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), call.ArgAt<CancellationToken>(1));
                    var file = call.Arg<ReviewRequest>().Files.Single();
                    return new ReviewResult(
                        $"Reviewed {file.Path}.",
                        [new InlineComment(file.Path, 1, "RIGHT", $"Issue in {file.Path}.", Severity.Warning)]);
                }
                finally
                {
                    Interlocked.Decrement(ref currentConcurrency);
                }
            });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        maxConcurrency.Should().BeGreaterThanOrEqualTo(expectedMinimumConcurrency);
        if (!supportsParallelRequests)
        {
            maxConcurrency.Should().Be(1);
        }
    }

    [Fact]
    public async Task FullFileContextDisabledDoesNotFetchFileContents()
    {
        await using var fixture = new WorkerFixture();
        ReviewRequest? capturedRequest = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedRequest = call.Arg<ReviewRequest>();
                return new ReviewResult("Reviewed.", []);
            });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await fixture.PullRequestFetcher.DidNotReceiveWithAnyArgs()
            .GetFileContentsAsync(default!, default!, default!, default!, default, default!, default);
        capturedRequest!.FullFileContents.Should().BeNull();
    }

    [Fact]
    public async Task RetrievalEnabledIndexesHeadShaAndPassesSnippetsToPrompt()
    {
        var activities = new List<DiagActivity>();
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

        await using var fixture = new WorkerFixture();
        var capturedRequest = new TaskCompletionSource<ReviewRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var indexCompleted = false;
        var config = ReviewConfig.Default with
        {
            Retrieval = ReviewConfig.Default.Retrieval with
            {
                Enabled = true,
                IndexCacheDir = "/tmp/reviewbot-worker-test-index"
            }
        };
        var snippet = new RepositoryContextSnippet(
            "src/IUsers.cs",
            4,
            4,
            "Task<User?> GetAsync(int id);");

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchMetadataAsync("octo-org", "reviewbot", 42, "install-token", Arg.Any<CancellationToken>())
            .Returns(new PullRequestMetadata(
                "Improve parser",
                "Adds coverage.",
                "base-sha",
                "snapshot-head",
                "https://github.com/octo-org/reviewbot.git"));
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile(
                "src/App.cs",
                """
                @@ -1,2 +1,3 @@
                 public Task<User?> FindAsync(int id)
                +    => repository.GetAsync(id);
                """,
                new HashSet<int> { 2 })]);
        fixture.RepoIndex.IsIndexedAsync(
                new RepoIndexKey("octo-org", "reviewbot", "snapshot-head"),
                Arg.Any<CancellationToken>())
            .Returns(false);
        fixture.WorkspaceFactory.CreateAsync(
                Arg.Is<WorkspaceRequest>(request =>
                    request.CloneUrl == "https://github.com/octo-org/reviewbot.git" &&
                    request.Sha == "snapshot-head" &&
                    request.InstallationToken == "install-token"),
                Arg.Any<CancellationToken>())
            .Returns(new TestWorkspace("/tmp/reviewbot-worker-test-workspace"));
        fixture.RepoIndex.IndexAsync(Arg.Any<RepoIndexRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                indexCompleted = true;
                return Task.CompletedTask;
            });
        fixture.RetrievalProvider.GetContextAsync(
                "octo-org",
                "reviewbot",
                Arg.Any<ReviewRequest>(),
                Arg.Any<PromptBudget>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                indexCompleted.Should().BeTrue();
                var budget = call.Arg<PromptBudget>().ConsumeAvailable("retrieval", 5, out _);
                return new RetrievalContextResult([snippet], budget);
            });
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedRequest.SetResult(call.Arg<ReviewRequest>());
                return new ReviewResult("retrieval reviewed", []);
            });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        var request = await capturedRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        fixture.RepoIndexFactory.Received(1).Create("/tmp/reviewbot-worker-test-index");
        await fixture.RepoIndex.Received(1).IndexAsync(
            Arg.Is<RepoIndexRequest>(request =>
                request.Owner == "octo-org" &&
                request.Repo == "reviewbot" &&
                request.Sha == "snapshot-head" &&
                request.RepositoryRoot == "/tmp/reviewbot-worker-test-workspace"),
            Arg.Any<CancellationToken>());
        request.RepositoryContext.Should().ContainSingle().Which.Should().Be(snippet);
        PromptBuilder.Build(request).UserPrompt.Should()
            .Contain("## Repository context")
            .And.Contain("Task<User?> GetAsync(int id);");

        List<DiagActivity> snapshot;
        lock (activities)
        {
            snapshot = [..activities];
        }

        var indexActivity = snapshot.Should()
            .Contain(activity => activity.OperationName == "reviewbot.retrieval.index_sha")
            .Which;
        indexActivity.GetTagItem("review.owner").Should().Be("octo-org");
        indexActivity.GetTagItem("review.repo").Should().Be("reviewbot");
        indexActivity.GetTagItem("review.sha").Should().Be("snapshot-head");
        indexActivity.GetTagItem("retrieval.index_mode").Should().Be("full");
    }

    [Fact]
    public async Task RetrievalEnabledIncrementallyIndexesHeadShaWhenBaseShaIsIndexed()
    {
        await using var fixture = new WorkerFixture();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = ReviewConfig.Default with
        {
            Retrieval = ReviewConfig.Default.Retrieval with
            {
                Enabled = true,
                IndexCacheDir = "/tmp/reviewbot-worker-test-index"
            }
        };

        fixture.PrReviewStateStore.GetLastShaAsync(98765, "octo-org/reviewbot", 42, Arg.Any<CancellationToken>())
            .Returns("old-sha");
        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchMetadataAsync("octo-org", "reviewbot", 42, "install-token", Arg.Any<CancellationToken>())
            .Returns(new PullRequestMetadata(
                "Improve parser",
                "Adds coverage.",
                "base-sha",
                "new-sha",
                "https://github.com/octo-org/reviewbot.git"));
        fixture.PullRequestFetcher.GetChangedFilesSinceAsync("octo-org", "reviewbot", "old-sha", "new-sha", "install-token", Arg.Any<CancellationToken>())
            .Returns(new ChangedFilesResult(["src/Changed.cs"], IsComplete: true));
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/Changed.cs")]);
        fixture.RepoIndex.IsIndexedAsync(
                new RepoIndexKey("octo-org", "reviewbot", "new-sha"),
                Arg.Any<CancellationToken>())
            .Returns(false);
        fixture.RepoIndex.IsIndexedAsync(
                new RepoIndexKey("octo-org", "reviewbot", "old-sha"),
                Arg.Any<CancellationToken>())
            .Returns(true);
        fixture.WorkspaceFactory.CreateAsync(
                Arg.Is<WorkspaceRequest>(request =>
                    request.CloneUrl == "https://github.com/octo-org/reviewbot.git" &&
                    request.Sha == "new-sha" &&
                    request.InstallationToken == "install-token"),
                Arg.Any<CancellationToken>())
            .Returns(new TestWorkspace("/tmp/reviewbot-worker-test-workspace"));
        fixture.RetrievalProvider.GetContextAsync(
                "octo-org",
                "reviewbot",
                Arg.Any<ReviewRequest>(),
                Arg.Any<PromptBudget>(),
                Arg.Any<CancellationToken>())
            .Returns(new RetrievalContextResult([], PromptBudget.Create(32768, 100, 0, 4096)));
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("incremental retrieval reviewed", []));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await fixture.RepoIndex.Received(1).IndexChangesAsync(
            Arg.Is<RepoIndexRequest>(request =>
                request.Owner == "octo-org" &&
                request.Repo == "reviewbot" &&
                request.Sha == "new-sha" &&
                request.RepositoryRoot == "/tmp/reviewbot-worker-test-workspace"),
            new RepoIndexKey("octo-org", "reviewbot", "old-sha"),
            Arg.Is<IReadOnlyCollection<string>>(paths => paths.Count == 1 && paths.Contains("src/Changed.cs")),
            Arg.Any<CancellationToken>());
        await fixture.RepoIndex.DidNotReceiveWithAnyArgs()
            .IndexAsync(default!, default);
    }

    [Fact]
    public async Task RetrievalIndexFallsBackToFullIndexWhenRepoConfigChanged()
    {
        await using var fixture = new WorkerFixture();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = ReviewConfig.Default with
        {
            Retrieval = ReviewConfig.Default.Retrieval with
            {
                Enabled = true,
                IndexCacheDir = "/tmp/reviewbot-worker-test-index"
            }
        };

        fixture.PrReviewStateStore.GetLastShaAsync(98765, "octo-org/reviewbot", 42, Arg.Any<CancellationToken>())
            .Returns("old-sha");
        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchMetadataAsync("octo-org", "reviewbot", 42, "install-token", Arg.Any<CancellationToken>())
            .Returns(new PullRequestMetadata(
                "Improve parser",
                "Adds coverage.",
                "base-sha",
                "new-sha",
                "https://github.com/octo-org/reviewbot.git"));
        fixture.PullRequestFetcher.GetChangedFilesSinceAsync("octo-org", "reviewbot", "old-sha", "new-sha", "install-token", Arg.Any<CancellationToken>())
            .Returns(new ChangedFilesResult([".github/review-bot.yml", "src/Changed.cs"], IsComplete: true));
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/Changed.cs")]);
        fixture.RepoIndex.IsIndexedAsync(
                new RepoIndexKey("octo-org", "reviewbot", "new-sha"),
                Arg.Any<CancellationToken>())
            .Returns(false);
        fixture.RepoIndex.IsIndexedAsync(
                new RepoIndexKey("octo-org", "reviewbot", "old-sha"),
                Arg.Any<CancellationToken>())
            .Returns(true);
        fixture.WorkspaceFactory.CreateAsync(Arg.Any<WorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TestWorkspace("/tmp/reviewbot-worker-test-workspace"));
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("full retrieval index reviewed", []));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await fixture.RepoIndex.Received(1).IndexAsync(
            Arg.Is<RepoIndexRequest>(request => request.Sha == "new-sha"),
            Arg.Any<CancellationToken>());
        await fixture.RepoIndex.DidNotReceiveWithAnyArgs()
            .IndexChangesAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task RequestChangesOnErrorPostsRequestChangesWhenFinalCommentHasErrorSeverity()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { RequestChangesOnError = true }
        };
        PullRequestReviewEvent? postedEvent = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Blocking issue.",
                [new InlineComment("src/App.cs", 1, "RIGHT", "This can corrupt data.", Severity.Error)]));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default, Arg.Any<PullRequestReviewEvent>())
            .ReturnsForAnyArgs(call =>
            {
                postedEvent = call.ArgAt<PullRequestReviewEvent>(8);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        postedEvent.Should().Be(PullRequestReviewEvent.RequestChanges);
    }

    [Fact]
    public async Task RequestChangesOnErrorKeepsCommentEventWhenOnlyWarningCommentsSurvive()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { RequestChangesOnError = true }
        };
        PullRequestReviewEvent? postedEvent = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Advisory issue.",
                [new InlineComment("src/App.cs", 1, "RIGHT", "Consider simplifying this.", Severity.Warning)]));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default, Arg.Any<PullRequestReviewEvent>())
            .ReturnsForAnyArgs(call =>
            {
                postedEvent = call.ArgAt<PullRequestReviewEvent>(8);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        postedEvent.Should().Be(PullRequestReviewEvent.Comment);
    }

    [Fact]
    public async Task ApproveIfCleanPostsApproveWhenNoCommentsSurvive()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { ApproveIfClean = true }
        };
        PullRequestReviewEvent? postedEvent = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("No issues found.", []));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default, Arg.Any<PullRequestReviewEvent>())
            .ReturnsForAnyArgs(call =>
            {
                postedEvent = call.ArgAt<PullRequestReviewEvent>(8);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        postedEvent.Should().Be(PullRequestReviewEvent.Approve);
    }

    [Fact]
    public async Task EmptyCommentSetKeepsCommentEventWhenApproveIfCleanIsDisabled()
    {
        await using var fixture = new WorkerFixture();
        PullRequestReviewEvent? postedEvent = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("No issues found.", []));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default, Arg.Any<PullRequestReviewEvent>())
            .ReturnsForAnyArgs(call =>
            {
                postedEvent = call.ArgAt<PullRequestReviewEvent>(8);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        postedEvent.Should().Be(PullRequestReviewEvent.Comment);
    }

    [Fact]
    public async Task JobExceptionIsLoggedAndDoesNotStopSubsequentJobs()
    {
        await using var fixture = new WorkerFixture();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadataCallCount = 0;

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchMetadataAsync(default!, default!, default, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                metadataCallCount++;
                return metadataCallCount == 1
                    ? Task.FromException<PullRequestMetadata>(new InvalidOperationException("fetch failed"))
                    : Task.FromResult(CreateMetadata());
            });
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("Recovered.", []));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob("delivery-1"), CancellationToken.None);
        await fixture.Queue.EnqueueAsync(CreateJob("delivery-2"), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        metadataCallCount.Should().Be(2);
    }

    [Fact]
    public async Task MetricsRecordSuccessStatusAfterSuccessfulJob()
    {
        var measurements = new List<(string status, long value)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ReviewBotMetrics.MeterName &&
                instrument.Name == "reviewbot.jobs.processed")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var status = tags.ToArray().FirstOrDefault(t => t.Key == "status").Value?.ToString() ?? "";
            measurements.Add((status, value));
        });
        listener.Start();

        await using var fixture = new WorkerFixture();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/A.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("Good.", []));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Give the metric recording (which happens after PostAsync returns) a moment to land
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        measurements.Should().ContainSingle()
            .Which.Should().Be(("success", 1L));
    }

    [Fact]
    public async Task MetricsRecordSkippedStatusWhenConfigDisabled()
    {
        var measurements = new List<(string status, long value)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ReviewBotMetrics.MeterName &&
                instrument.Name == "reviewbot.jobs.processed")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var status = tags.ToArray().FirstOrDefault(t => t.Key == "status").Value?.ToString() ?? "";
            measurements.Add((status, value));
        });
        listener.Start();

        await using var fixture = new WorkerFixture();
        var configFetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                configFetched.SetResult();
                return ReviewConfig.Default with { Enabled = false };
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);
        await configFetched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        measurements.Should().ContainSingle()
            .Which.Should().Be(("skipped", 1L));
    }

    [Fact]
    public async Task MetricsRecordFailureStatusWhenJobThrows()
    {
        var measurements = new List<(string status, long value)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ReviewBotMetrics.MeterName &&
                instrument.Name == "reviewbot.jobs.processed")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var status = tags.ToArray().FirstOrDefault(t => t.Key == "status").Value?.ToString() ?? "";
            measurements.Add((status, value));
        });
        listener.Start();

        await using var fixture = new WorkerFixture();
        var secondJobPosted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var configCallCount = 0;

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                configCallCount++;
                return configCallCount == 1
                    ? Task.FromException<ReviewConfig>(new InvalidOperationException("config fetch failed"))
                    : Task.FromResult(ReviewConfig.Default);
            });
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/A.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("Ok.", []));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                secondJobPosted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob("delivery-fail"), CancellationToken.None);
        await fixture.Queue.EnqueueAsync(CreateJob("delivery-ok"), CancellationToken.None);
        await secondJobPosted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        measurements.Should().HaveCount(2);
        measurements.Should().ContainSingle(m => m.status == "failure" && m.value == 1L);
        measurements.Should().ContainSingle(m => m.status == "success" && m.value == 1L);
    }

    [Fact]
    public async Task MetricsRecordLlmDurationAndCommentsPostedAfterReview()
    {
        var durationMeasurements = new List<(string provider, double value)>();
        var commentsMeasurements = new List<int>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ReviewBotMetrics.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "reviewbot.llm.duration_ms")
            {
                var provider = tags.ToArray().FirstOrDefault(t => t.Key == "provider").Value?.ToString() ?? "";
                durationMeasurements.Add((provider, value));
            }
        });
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "reviewbot.review.comments_posted")
                commentsMeasurements.Add(value);
        });
        listener.Start();

        await using var fixture = new WorkerFixture();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/A.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Found one issue.",
                [new InlineComment("src/A.cs", 1, "RIGHT", "This should handle null input before dereferencing the value.", Severity.Warning)]));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        durationMeasurements.Should().ContainSingle()
            .Which.provider.Should().Be("openai");
        durationMeasurements[0].value.Should().BeGreaterThanOrEqualTo(0);

        commentsMeasurements.Should().ContainSingle()
            .Which.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrencyOptionFansOutJobsInParallel()
    {
        const int concurrency = 5;
        await using var fixture = new WorkerFixture(concurrency: concurrency);

        var barrier = new Barrier(concurrency);
        var completedCount = 0;
        var allPosted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/A.cs")]);

        // The Barrier.SignalAndWait blocks until all `concurrency` jobs reach this point simultaneously,
        // proving they are running in parallel rather than sequentially.
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(5));
                return Task.FromResult(new ReviewResult("OK.", []));
            });

        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                if (Interlocked.Increment(ref completedCount) == concurrency)
                {
                    allPosted.SetResult();
                }

                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        for (var i = 0; i < concurrency; i++)
        {
            await fixture.Queue.EnqueueAsync(CreateJob($"delivery-{i}"), CancellationToken.None);
        }

        await allPosted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        completedCount.Should().Be(concurrency);
    }

    [Fact]
    public async Task GroundingProviderCalledWithCorrectOwnerRepoAndSha()
    {
        await using var fixture = new WorkerFixture();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GroundingRequest? capturedGroundingRequest = null;

        fixture.RepoConfigFetcher.FetchAsync("octo-org", "reviewbot", "event-head", "install-token", Arg.Any<CancellationToken>())
            .Returns(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync("octo-org", "reviewbot", 42, "install-token", 50, Arg.Any<IReadOnlySet<string>?>(), Arg.Any<CancellationToken>())
            .Returns([CreateFile("src/App.cs")]);
        fixture.GroundingProvider.GetContextAsync(Arg.Any<GroundingRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedGroundingRequest = call.Arg<GroundingRequest>();
                return new GroundingContext(null, null, null);
            });
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("ok", []));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        capturedGroundingRequest.Should().NotBeNull();
        capturedGroundingRequest!.Owner.Should().Be("octo-org");
        capturedGroundingRequest.Repo.Should().Be("reviewbot");
        capturedGroundingRequest.HeadSha.Should().Be("snapshot-head");
        capturedGroundingRequest.InstallationToken.Should().Be("install-token");
    }

    [Fact]
    public async Task GroundingContextPassedToLlmViaReviewRequest()
    {
        await using var fixture = new WorkerFixture();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expectedGrounding = new GroundingContext(
            new LanguageMetadata("dotnet", "10.0", null, []),
            Build: null,
            Tests: null);
        ReviewRequest? capturedRequest = null;

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.GroundingProvider.GetContextAsync(Arg.Any<GroundingRequest>(), Arg.Any<CancellationToken>())
            .Returns(expectedGrounding);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedRequest = call.Arg<ReviewRequest>();
                return new ReviewResult("grounded", []);
            });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        capturedRequest!.Grounding.Should().BeSameAs(expectedGrounding);
    }

    [Fact]
    public async Task EmptyDiffSkipsGroundingAndLlmAndPost()
    {
        await using var fixture = new WorkerFixture();
        var filesFetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                filesFetched.SetResult();
                return Array.Empty<FileChange>();
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await filesFetched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        await fixture.GroundingProvider.DidNotReceiveWithAnyArgs()
            .GetContextAsync(default!, default);
        fixture.LlmFactory.DidNotReceiveWithAnyArgs().Create(default!);
        await fixture.ReviewPoster.DidNotReceiveWithAnyArgs()
            .PostAsync(default!, default!, default, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task AllFilesIgnoredSkipsGroundingAndLlmAndPost()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with { Ignore = ["**"] };
        var filesFetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                filesFetched.SetResult();
                return new[] { CreateFile("src/App.cs"), CreateFile("docs/readme.md") };
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await filesFetched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        await fixture.GroundingProvider.DidNotReceiveWithAnyArgs()
            .GetContextAsync(default!, default);
        fixture.LlmFactory.DidNotReceiveWithAnyArgs().Create(default!);
        await fixture.ReviewPoster.DidNotReceiveWithAnyArgs()
            .PostAsync(default!, default!, default, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task PatchBudgetRemovesAllFilesSkipsGroundingAndLlmAndPost()
    {
        await using var fixture = new WorkerFixture();
        // MaxPatchLines=1 means budget=5 chars; two 6-line files both exceed the budget individually,
        // so the greedy selection loop picks neither.
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { MaxPatchLines = 1, ChunkedReview = false }
        };
        var filesFetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                filesFetched.SetResult();
                return new[]
                {
                    CreateFile("src/A.cs", patchLines: 6),
                    CreateFile("src/B.cs", patchLines: 6)
                };
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await filesFetched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        await fixture.GroundingProvider.DidNotReceiveWithAnyArgs()
            .GetContextAsync(default!, default);
        fixture.LlmFactory.DidNotReceiveWithAnyArgs().Create(default!);
        await fixture.ReviewPoster.DidNotReceiveWithAnyArgs()
            .PostAsync(default!, default!, default, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task MinConfidenceMediumFiltersLowConfidenceCommentsBeforePosting()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { MinConfidence = Confidence.Medium }
        };
        ReviewResult? postedResult = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Mixed confidence.",
                [
                    new InlineComment("src/App.cs", 1, "RIGHT", "High confidence.", Severity.Error, Confidence.High),
                    new InlineComment("src/App.cs", 2, "RIGHT", "Medium confidence.", Severity.Warning, Confidence.Medium),
                    new InlineComment("src/App.cs", 3, "RIGHT", "Low confidence.", Severity.Info, Confidence.Low)
                ]));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postedResult = call.ArgAt<ReviewResult>(4);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        postedResult!.Comments.Should().HaveCount(2);
        postedResult.Comments.Should().NotContain(c => c.Confidence == Confidence.Low);
        postedResult.Comments.Should().Contain(c => c.Confidence == Confidence.High);
        postedResult.Comments.Should().Contain(c => c.Confidence == Confidence.Medium);
    }

    [Fact]
    public async Task MinConfidenceHighRetainsOnlyHighConfidenceComments()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { MinConfidence = Confidence.High }
        };
        ReviewResult? postedResult = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "High threshold.",
                [
                    new InlineComment("src/App.cs", 1, "RIGHT", "High.", Severity.Error, Confidence.High),
                    new InlineComment("src/App.cs", 2, "RIGHT", "Medium.", Severity.Warning, Confidence.Medium),
                    new InlineComment("src/App.cs", 3, "RIGHT", "Low.", Severity.Info, Confidence.Low)
                ]));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postedResult = call.ArgAt<ReviewResult>(4);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        postedResult!.Comments.Should().ContainSingle()
            .Which.Confidence.Should().Be(Confidence.High);
    }

    [Fact]
    public async Task PraiseOnlyCommentsAndCleanSummariesAreDroppedBeforePosting()
    {
        await using var fixture = new WorkerFixture();
        ReviewResult? postedResult = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Looks good.",
                [
                    new InlineComment("src/App.cs", 1, "RIGHT", "Nice guard.", Severity.Info, Confidence.High),
                    new InlineComment("src/App.cs", 2, "RIGHT", "The test correctly validates the important behavior.", Severity.Info, Confidence.Medium),
                    new InlineComment("src/App.cs", 3, "RIGHT", "This should handle null input before dereferencing the value.", Severity.Warning, Confidence.High)
                ]));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postedResult = call.ArgAt<ReviewResult>(4);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        postedResult!.Comments.Should().ContainSingle()
            .Which.Body.Should().Be("This should handle null input before dereferencing the value.");
        postedResult.Summary.Should().NotContain("Looks good.");
    }

    [Fact]
    public async Task SpeculativeMissingContractCommentsAreDroppedBeforePosting()
    {
        await using var fixture = new WorkerFixture();
        ReviewResult? postedResult = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Review complete.",
                [
                    new InlineComment(
                        "src/App.cs",
                        1,
                        "RIGHT",
                        "Transaction signing and execution are performed synchronously without awaiting the sign operation. If _walletManager.SignTransaction() is async, this will block the thread. Verify the signature method's return type and ensure proper async/await usage to avoid deadlocks in hosted environments.",
                        Severity.Warning,
                        Confidence.High),
                    new InlineComment(
                        "src/App.cs",
                        2,
                        "RIGHT",
                        "This should handle null input before dereferencing the value.",
                        Severity.Warning,
                        Confidence.High)
                ]));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postedResult = call.ArgAt<ReviewResult>(4);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        postedResult!.Comments.Should().ContainSingle()
            .Which.Body.Should().Be("This should handle null input before dereferencing the value.");
    }

    [Fact]
    public async Task ExplicitMissingDiffContextCommentsAreDroppedBeforePosting()
    {
        await using var fixture = new WorkerFixture();
        ReviewResult? postedResult = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Review complete.",
                [
                    new InlineComment(
                        "src/App.cs",
                        1,
                        "RIGHT",
                        "WithRecalculatedConfidence() is called but its implementation isn't visible in this diff. Verify it correctly recalculates confidence scores.",
                        Severity.Warning,
                        Confidence.High),
                    new InlineComment(
                        "src/App.cs",
                        2,
                        "RIGHT",
                        "This should handle null input before dereferencing the value.",
                        Severity.Warning,
                        Confidence.High)
                ]));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postedResult = call.ArgAt<ReviewResult>(4);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        postedResult!.Comments.Should().ContainSingle()
            .Which.Body.Should().Be("This should handle null input before dereferencing the value.");
    }

    [Fact]
    public async Task NonActionableProcessCommentsAreDroppedBeforePosting()
    {
        await using var fixture = new WorkerFixture();
        ReviewResult? postedResult = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Review complete.",
                [
                    new InlineComment(
                        "src/App.cs",
                        1,
                        "RIGHT",
                        "This is intentional for the chunking path, but consider adding a comment explaining that ApplyPatchBudget is intentionally skipped when chunking is enabled.",
                        Severity.Info,
                        Confidence.High),
                    new InlineComment(
                        "src/App.cs",
                        2,
                        "RIGHT",
                        "This is the correct behavior to avoid repeating PR overview once per chunk.",
                        Severity.Info,
                        Confidence.High),
                    new InlineComment(
                        "src/App.cs",
                        3,
                        "RIGHT",
                        "The chunk_headroom config field allows tuning this, but consider whether the headroom could lead to suboptimal packing.",
                        Severity.Info,
                        Confidence.High),
                    new InlineComment(
                        "src/App.cs",
                        4,
                        "RIGHT",
                        "This should handle null input before dereferencing the value.",
                        Severity.Warning,
                        Confidence.High)
                ]));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postedResult = call.ArgAt<ReviewResult>(4);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        postedResult!.Comments.Should().ContainSingle()
            .Which.Body.Should().Be("This should handle null input before dereferencing the value.");
    }

    [Fact]
    public async Task EvalFixtureMetaCommentsAreDroppedBeforePosting()
    {
        await using var fixture = new WorkerFixture();
        ReviewResult? postedResult = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("tests/ReviewBot.Evals/Fixtures/001/repo-state/src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Review complete.",
                [
                    new InlineComment(
                        "tests/ReviewBot.Evals/Fixtures/001/repo-state/src/App.cs",
                        1,
                        "RIGHT",
                        "The fixture correctly models a security boundary leak: returning whether the secret is blank leaks configuration state across the webhook trust boundary. The expected.yaml requires mentioning 'leak', 'trust boundary', or 'secret'.",
                        Severity.Warning,
                        Confidence.High),
                    new InlineComment(
                        "tests/ReviewBot.Evals/Fixtures/001/repo-state/src/App.cs",
                        2,
                        "RIGHT",
                        "This should handle null input before dereferencing the value.",
                        Severity.Warning,
                        Confidence.High)
                ]));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postedResult = call.ArgAt<ReviewResult>(4);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        postedResult!.Comments.Should().ContainSingle()
            .Which.Body.Should().Be("This should handle null input before dereferencing the value.");
    }

    [Fact]
    public async Task AgenticContextDisabledDoesNotFetchRequestedFiles()
    {
        await using var fixture = new WorkerFixture();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Initial.",
                [],
                [new ContextRequest("src/IFoo.cs", "interface contract")]));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await fixture.PullRequestFetcher.DidNotReceiveWithAnyArgs()
            .GetFileContentsAsync(default!, default!, default!, default!, default, default!, default);
        await fixture.Llm.DidNotReceiveWithAnyArgs()
            .CompleteRawAsync(default!, default, default!);
    }

    [Fact]
    public async Task AgenticContextFetchesFilesAndUsesSecondPassResult()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { AgenticContext = true }
        };
        IReadOnlyList<ContextRequest>? fetchedRequests = null;
        PromptPayload? enrichedPrompt = null;
        string? enrichedPhase = null;
        ReviewResult? postedResult = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Initial summary.",
                [new InlineComment("src/App.cs", 1, "RIGHT", "Initial comment.", Severity.Info)],
                [
                    new ContextRequest("src/IFoo.cs", "contract"),
                    new ContextRequest("src/Base.cs", "base class")
                ]));
        fixture.PullRequestFetcher.GetFileContentsAsync(default!, default!, default!, default!, default, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                fetchedRequests = call.ArgAt<IReadOnlyList<ContextRequest>>(2);
                return new List<(string Path, string Content)>
                {
                    ("src/IFoo.cs", "public interface IFoo { void Save(); }"),
                    ("src/Base.cs", "public abstract class Base { }")
                };
            });
        fixture.Llm.CompleteRawAsync(Arg.Any<PromptPayload>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(call =>
            {
                enrichedPrompt = call.Arg<PromptPayload>();
                enrichedPhase = call.ArgAt<string>(2);
                return """
                {
                  "summary": "Final summary.",
                  "comments": [
                    {
                      "path": "src/App.cs",
                      "line": 1,
                      "side": "RIGHT",
                      "severity": "error",
                      "confidence": "high",
                      "body": "Final context-informed comment."
                    }
                  ]
                }
                """;
            });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postedResult = call.ArgAt<ReviewResult>(4);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        fetchedRequests!.Select(request => request.Path).Should().Equal("src/IFoo.cs", "src/Base.cs");
        enrichedPrompt.Should().NotBeNull();
        enrichedPhase.Should().Be("agentic_context");
        enrichedPrompt!.UserPrompt.Should().Contain("public interface IFoo");
        enrichedPrompt.UserPrompt.Should().Contain("Initial comment.");
        postedResult!.Summary.Should().StartWith("Final summary.");
        postedResult.Comments.Should().ContainSingle()
            .Which.Body.Should().Be("Final context-informed comment.");
    }

    [Fact]
    public async Task AgenticContextFiltersUnsafeIgnoredDuplicateAndOverCapRequestsBeforeFetching()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with
            {
                AgenticContext = true,
                MaxContextRequests = 2
            },
            Ignore = ["docs/**"]
        };
        IReadOnlyList<ContextRequest>? fetchedRequests = null;
        ReviewResult? postedResult = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Initial.",
                [new InlineComment("src/App.cs", 1, "RIGHT", "Initial stays.", Severity.Info)],
                [
                    new ContextRequest("../bad.cs", null),
                    new ContextRequest("/absolute.cs", null),
                    new ContextRequest(@"src\Bad.cs", null),
                    new ContextRequest("docs/Guide.md", null),
                    new ContextRequest("src/Good.cs", null),
                    new ContextRequest("src/Good.cs", null),
                    new ContextRequest(".env", null),
                    new ContextRequest("certs/prod.pem", null),
                    new ContextRequest("src/Second.cs", null),
                    new ContextRequest("src/Third.cs", null)
                ]));
        fixture.PullRequestFetcher.GetFileContentsAsync(default!, default!, default!, default!, default, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                fetchedRequests = call.ArgAt<IReadOnlyList<ContextRequest>>(2);
                return Array.Empty<(string Path, string Content)>();
            });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postedResult = call.ArgAt<ReviewResult>(4);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        fetchedRequests!.Select(request => request.Path).Should().Equal("src/Good.cs", "src/Second.cs");
        await fixture.Llm.DidNotReceiveWithAnyArgs()
            .CompleteRawAsync(default!, default, default!);
        postedResult!.Comments.Should().ContainSingle()
            .Which.Body.Should().Be("Initial stays.");
    }

    [Fact]
    public async Task AgenticContextSkipsSecondPassWhenNoFilesAreFetched()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { AgenticContext = true }
        };
        ReviewResult? postedResult = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Initial.",
                [new InlineComment("src/App.cs", 1, "RIGHT", "Initial survives.", Severity.Info)],
                [new ContextRequest("src/Missing.cs", null)]));
        fixture.PullRequestFetcher.GetFileContentsAsync(default!, default!, default!, default!, default, default!, default)
            .ReturnsForAnyArgs(Array.Empty<(string Path, string Content)>());
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postedResult = call.ArgAt<ReviewResult>(4);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await fixture.Llm.DidNotReceiveWithAnyArgs()
            .CompleteRawAsync(default!, default, default!);
        postedResult!.Comments.Should().ContainSingle()
            .Which.Body.Should().Be("Initial survives.");
    }

    [Fact]
    public async Task AgenticContextSecondPassFailurePostsInitialComments()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { AgenticContext = true }
        };
        ReviewResult? postedResult = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Initial.",
                [new InlineComment("src/App.cs", 1, "RIGHT", "Initial survives failure.", Severity.Info)],
                [new ContextRequest("src/IFoo.cs", null)]));
        fixture.PullRequestFetcher.GetFileContentsAsync(default!, default!, default!, default!, default, default!, default)
            .ReturnsForAnyArgs([("src/IFoo.cs", "public interface IFoo {}")]);
        fixture.Llm.CompleteRawAsync(Arg.Any<PromptPayload>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .ThrowsAsync(new InvalidOperationException("context LLM failed"));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postedResult = call.ArgAt<ReviewResult>(4);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        postedResult!.Comments.Should().ContainSingle()
            .Which.Body.Should().Be("Initial survives failure.");
    }

    [Fact]
    public async Task SelfCritiqueStartsWhileAgenticContextFetchIsInFlightWhenInitialResultSurvives()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with
            {
                AgenticContext = true,
                SelfCritique = true
            }
        };
        var agenticFetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var critiqueStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<IReadOnlyList<(string Path, string Content)>> FetchEmptyContextAfterCritiqueStartsAsync()
        {
            agenticFetchStarted.SetResult();
            await critiqueStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            return [];
        }

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Initial.",
                [new InlineComment("src/App.cs", 1, "RIGHT", "Medium survives.", Severity.Warning, Confidence.Medium)],
                [new ContextRequest("src/Missing.cs", null)]));
        fixture.PullRequestFetcher
            .GetFileContentsAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ContextRequest>>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => FetchEmptyContextAfterCritiqueStartsAsync());
        fixture.Llm.CompleteRawAsync(Arg.Any<PromptPayload>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .ReturnsForAnyArgs(async call =>
            {
                call.ArgAt<string>(2).Should().Be("self_critique");
                critiqueStarted.SetResult();
                await agenticFetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                return """{"retained_indices":[0],"rationale":"keep"}""";
            });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task EnrichedAgenticResultGetsItsOwnSelfCritique()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with
            {
                AgenticContext = true,
                SelfCritique = true
            }
        };
        var initialCritiqueStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ReviewResult? postedResult = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Initial.",
                [new InlineComment("src/App.cs", 1, "RIGHT", "Initial medium.", Severity.Warning, Confidence.Medium)],
                [new ContextRequest("src/IFoo.cs", null)]));
        fixture.PullRequestFetcher.GetFileContentsAsync(default!, default!, default!, default!, default, default!, default)
            .ReturnsForAnyArgs([("src/IFoo.cs", "public interface IFoo {}")]);
        fixture.Llm.CompleteRawAsync(Arg.Any<PromptPayload>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .ReturnsForAnyArgs(async call =>
            {
                var phase = call.ArgAt<string>(2);
                var prompt = call.Arg<PromptPayload>();
                if (phase == "agentic_context")
                {
                    return """
                    {
                      "summary": "Final summary.",
                      "comments": [
                        {
                          "path": "src/App.cs",
                          "line": 1,
                          "side": "RIGHT",
                          "severity": "warning",
                          "confidence": "medium",
                          "body": "Final medium kept."
                        },
                        {
                          "path": "src/App.cs",
                          "line": 2,
                          "side": "RIGHT",
                          "severity": "info",
                          "confidence": "low",
                          "body": "Final low dropped."
                        }
                      ]
                    }
                    """;
                }

                phase.Should().Be("self_critique");
                if (prompt.UserPrompt.Contains("Initial medium.", StringComparison.Ordinal))
                {
                    initialCritiqueStarted.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(1));
                    return """{"retained_indices":[0],"rationale":"canceled"}""";
                }

                prompt.UserPrompt.Should().Contain("Final medium kept.");
                prompt.UserPrompt.Should().Contain("Final low dropped.");
                return """{"retained_indices":[0],"rationale":"keep final warning only"}""";
            });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postedResult = call.ArgAt<ReviewResult>(4);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await initialCritiqueStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        postedResult!.Comments.Should().ContainSingle()
            .Which.Body.Should().Be("Final medium kept.");
    }

    [Fact]
    public async Task SelfCritiqueDisabledDoesNotCallRawCompletion()
    {
        await using var fixture = new WorkerFixture();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Review complete.",
                [new InlineComment("src/App.cs", 1, "RIGHT", "Maybe issue.", Severity.Warning, Confidence.Medium)]));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await fixture.Llm.DidNotReceiveWithAnyArgs()
            .CompleteRawAsync(default!, default, default!);
    }

    [Fact]
    public async Task SelfCritiqueSkipsRawCompletionWhenAllSurvivingCommentsAreHighConfidence()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { SelfCritique = true }
        };
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Review complete.",
                [new InlineComment("src/App.cs", 1, "RIGHT", "Certain issue.", Severity.Error, Confidence.High)]));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await fixture.Llm.DidNotReceiveWithAnyArgs()
            .CompleteRawAsync(default!, default, default!);
    }

    [Fact]
    public async Task SelfCritiqueRetainsSelectedLowerConfidenceCommentsAndAllHighConfidenceComments()
    {
        ReviewTrace? capturedTrace = null;
        var traceWriter = Substitute.For<IReviewTraceWriter>();
        traceWriter.WriteAsync(Arg.Any<ReviewTrace>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedTrace = call.Arg<ReviewTrace>();
                return Task.CompletedTask;
            });

        await using var fixture = new WorkerFixture(traceWriter: traceWriter);
        // Pin MinConfidence=Low so the low-confidence comment survives the confidence
        // filter and reaches self-critique (the behavior under test); the default is
        // now Medium, which would drop it earlier.
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { SelfCritique = true, MinConfidence = Confidence.Low }
        };
        ReviewResult? postedResult = null;
        PromptPayload? critiquePrompt = null;
        string? critiquePhase = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Mixed confidence.",
                [
                    new InlineComment("src/App.cs", 1, "RIGHT", "High stays.", Severity.Error, Confidence.High),
                    new InlineComment("src/App.cs", 2, "RIGHT", "Medium kept.", Severity.Warning, Confidence.Medium),
                    new InlineComment("src/App.cs", 3, "RIGHT", "Low dropped.", Severity.Info, Confidence.Low),
                    new InlineComment("src/App.cs", 4, "RIGHT", "Medium kept too.", Severity.Warning, Confidence.Medium)
                ]));
        fixture.Llm.CompleteRawAsync(Arg.Any<PromptPayload>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(call =>
            {
                critiquePrompt = call.Arg<PromptPayload>();
                critiquePhase = call.ArgAt<string>(2);
                return """{"retained_indices":[0,2],"rationale":"drop low-confidence duplicate"}""";
            });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postedResult = call.ArgAt<ReviewResult>(4);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        postedResult!.Comments.Select(c => c.Body)
            .Should().Equal("High stays.", "Medium kept.", "Medium kept too.");
        capturedTrace.Should().NotBeNull();
        capturedTrace!.DroppedComments.Should().ContainSingle(c =>
            c.Body == "Low dropped." &&
            c.Reason == "self_critique");
        critiquePrompt.Should().NotBeNull();
        critiquePhase.Should().Be("self_critique");
        critiquePrompt!.UserPrompt.Should().Contain("0. src/App.cs:2");
        critiquePrompt.UserPrompt.Should().Contain("1. src/App.cs:3");
        critiquePrompt.UserPrompt.Should().Contain("2. src/App.cs:4");
        critiquePrompt.UserPrompt.Should().NotContain("High stays.");
    }

    [Fact]
    public async Task SelfCritiqueFailurePostsAllCandidateComments()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { SelfCritique = true }
        };
        ReviewResult? postedResult = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Mixed confidence.",
                [
                    new InlineComment("src/App.cs", 1, "RIGHT", "High stays.", Severity.Error, Confidence.High),
                    new InlineComment("src/App.cs", 2, "RIGHT", "Medium survives failure.", Severity.Warning, Confidence.Medium)
                ]));
        fixture.Llm.CompleteRawAsync(Arg.Any<PromptPayload>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .ThrowsAsync(new InvalidOperationException("critique unavailable"));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postedResult = call.ArgAt<ReviewResult>(4);
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        postedResult!.Comments.Select(c => c.Body)
            .Should().Equal("High stays.", "Medium survives failure.");
    }

    [Fact]
    public async Task SelfCritiqueSkipsRawCompletionWhenInitialCommentListIsEmpty()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { SelfCritique = true }
        };
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("No comments.", []));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await fixture.Llm.DidNotReceiveWithAnyArgs()
            .CompleteRawAsync(default!, default, default!);
    }

    [Fact]
    public async Task GroundingReturningEmptyContextDoesNotFailTheJob()
    {
        await using var fixture = new WorkerFixture();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.GroundingProvider.GetContextAsync(Arg.Any<GroundingRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GroundingContext(null, null, null));
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("no grounding, still reviewed", []));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await fixture.ReviewPoster.ReceivedWithAnyArgs(1)
            .PostAsync(default!, default!, default, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task FirstReviewStoresHeadShaAfterPosting()
    {
        await using var fixture = new WorkerFixture();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/A.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("ok", []));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        await fixture.PrReviewStateStore.Received(1)
            .SetLastShaAsync(98765, "octo-org/reviewbot", 42, "snapshot-head", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeltaReviewComparesFromLastShaAndFetchesOnlyChangedFiles()
    {
        await using var fixture = new WorkerFixture();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlySet<string>? capturedAllowlist = null;
        ReviewRequest? capturedRequest = null;

        fixture.PrReviewStateStore.GetLastShaAsync(98765, "octo-org/reviewbot", 42, Arg.Any<CancellationToken>())
            .Returns("old-sha");
        fixture.PullRequestFetcher.FetchMetadataAsync("octo-org", "reviewbot", 42, "install-token", Arg.Any<CancellationToken>())
            .Returns(new PullRequestMetadata("PR title", "PR body", "base-sha", "new-sha"));
        fixture.PullRequestFetcher.GetChangedFilesSinceAsync("octo-org", "reviewbot", "old-sha", "new-sha", "install-token", Arg.Any<CancellationToken>())
            .Returns(new ChangedFilesResult(["src/Changed.cs"], IsComplete: true));
        fixture.PullRequestFetcher.FetchFilesAsync("octo-org", "reviewbot", 42, "install-token", 50, Arg.Any<IReadOnlySet<string>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedAllowlist = call.ArgAt<IReadOnlySet<string>?>(5);
                return [CreateFile("src/Changed.cs")];
            });
        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedRequest = call.Arg<ReviewRequest>();
                return new ReviewResult("delta review ok", []);
            });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        capturedAllowlist.Should().BeEquivalentTo(new[] { "src/Changed.cs" });
        capturedRequest!.Files.Select(f => f.Path).Should().Equal("src/Changed.cs");
        capturedRequest.HeadSha.Should().Be("new-sha");
        await fixture.PrReviewStateStore.Received(1)
            .SetLastShaAsync(98765, "octo-org/reviewbot", 42, "new-sha", Arg.Any<CancellationToken>());
        await fixture.PullRequestFetcher.Received(1)
            .GetChangedFilesSinceAsync("octo-org", "reviewbot", "old-sha", "new-sha", "install-token", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeltaReviewWithNoChangedFilesSkipsLlmAndUpdatesShа()
    {
        await using var fixture = new WorkerFixture();
        var compareReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.PrReviewStateStore.GetLastShaAsync(98765, "octo-org/reviewbot", 42, Arg.Any<CancellationToken>())
            .Returns("old-sha");
        fixture.PullRequestFetcher.FetchMetadataAsync("octo-org", "reviewbot", 42, "install-token", Arg.Any<CancellationToken>())
            .Returns(new PullRequestMetadata("PR title", "PR body", "base-sha", "new-sha"));
        fixture.PullRequestFetcher.GetChangedFilesSinceAsync("octo-org", "reviewbot", "old-sha", "new-sha", "install-token", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                compareReturned.SetResult();
                return new ChangedFilesResult([], IsComplete: true);
            });
        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);

        await compareReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        fixture.LlmFactory.DidNotReceiveWithAnyArgs().Create(default!);
        await fixture.ReviewPoster.DidNotReceiveWithAnyArgs()
            .PostAsync(default!, default!, default, default!, default!, default!, default!, default);
        await fixture.PrReviewStateStore.Received(1)
            .SetLastShaAsync(98765, "octo-org/reviewbot", 42, "new-sha", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompareApiThrowingFallsBackToFullFileListAndProceedsWithReview()
    {
        await using var fixture = new WorkerFixture();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.PrReviewStateStore.GetLastShaAsync(98765, "octo-org/reviewbot", 42, Arg.Any<CancellationToken>())
            .Returns("old-sha");
        fixture.PullRequestFetcher.FetchMetadataAsync("octo-org", "reviewbot", 42, "install-token", Arg.Any<CancellationToken>())
            .Returns(new PullRequestMetadata("PR title", "PR body", "base-sha", "new-sha"));
        fixture.PullRequestFetcher.GetChangedFilesSinceAsync(default!, default!, default!, default!, default!, default)
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("GitHub compare API failed"));
        fixture.PullRequestFetcher.FetchFilesAsync("octo-org", "reviewbot", 42, "install-token", 50, Arg.Is<IReadOnlySet<string>?>(x => x == null), Arg.Any<CancellationToken>())
            .Returns([CreateFile("src/A.cs")]);
        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("fallback review ok", []));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await fixture.ReviewPoster.ReceivedWithAnyArgs(1)
            .PostAsync(default!, default!, default, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task CompareApiTruncatedFallsBackToFullFileListAndProceedsWithReview()
    {
        await using var fixture = new WorkerFixture();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlySet<string>? capturedAllowlist = null;

        fixture.PrReviewStateStore.GetLastShaAsync(98765, "octo-org/reviewbot", 42, Arg.Any<CancellationToken>())
            .Returns("old-sha");
        fixture.PullRequestFetcher.FetchMetadataAsync("octo-org", "reviewbot", 42, "install-token", Arg.Any<CancellationToken>())
            .Returns(new PullRequestMetadata("PR title", "PR body", "base-sha", "new-sha"));
        // Simulate GitHub's 300-file cap: IsComplete = false
        var truncatedPaths = Enumerable.Range(1, 300).Select(i => $"src/File{i}.cs").ToList();
        fixture.PullRequestFetcher.GetChangedFilesSinceAsync(default!, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(new ChangedFilesResult(truncatedPaths, IsComplete: false));
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                capturedAllowlist = call.ArgAt<IReadOnlySet<string>?>(5);
                return [CreateFile("src/A.cs")];
            });
        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("truncated fallback ok", []));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Must use null allowlist (full file list) when compare is truncated
        capturedAllowlist.Should().BeNull();
        await fixture.ReviewPoster.ReceivedWithAnyArgs(1)
            .PostAsync(default!, default!, default, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task DeltaReviewAllowlistFetchesFileBeyondFirstPageWindow()
    {
        // This test verifies that when an allowlist is used, FetchFilesAsync receives
        // the allowlist so it can page past the first window to find the delta file.
        await using var fixture = new WorkerFixture();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlySet<string>? capturedAllowlist = null;

        fixture.PrReviewStateStore.GetLastShaAsync(98765, "octo-org/reviewbot", 42, Arg.Any<CancellationToken>())
            .Returns("old-sha");
        fixture.PullRequestFetcher.FetchMetadataAsync("octo-org", "reviewbot", 42, "install-token", Arg.Any<CancellationToken>())
            .Returns(new PullRequestMetadata("PR title", "PR body", "base-sha", "new-sha"));
        // Delta file is "deep" in the PR (would be past first page in a non-allowlist fetch)
        fixture.PullRequestFetcher.GetChangedFilesSinceAsync(default!, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(new ChangedFilesResult(["src/Deep/PageTwoFile.cs"], IsComplete: true));
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                capturedAllowlist = call.ArgAt<IReadOnlySet<string>?>(5);
                return [CreateFile("src/Deep/PageTwoFile.cs")];
            });
        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("deep file reviewed", []));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        capturedAllowlist.Should().BeEquivalentTo(new[] { "src/Deep/PageTwoFile.cs" });
        await fixture.ReviewPoster.ReceivedWithAnyArgs(1)
            .PostAsync(default!, default!, default, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task TraceWriterIsCalledWithReviewDataAfterPosting()
    {
        ReviewTrace? capturedTrace = null;
        var traceWriter = Substitute.For<IReviewTraceWriter>();
        traceWriter.WriteAsync(Arg.Any<ReviewTrace>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedTrace = call.Arg<ReviewTrace>();
                return Task.CompletedTask;
            });

        await using var fixture = new WorkerFixture(traceWriter: traceWriter);
        var postOrder = new List<string>();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/A.cs"), CreateFile("src/B.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Found an issue.",
                [new InlineComment("src/A.cs", 3, "RIGHT", "Null dereference risk.", Severity.Error)]));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                postOrder.Add("post");
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // Give trace write (which runs after post) time to execute
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        await traceWriter.Received(1).WriteAsync(Arg.Any<ReviewTrace>(), Arg.Any<CancellationToken>());

        capturedTrace.Should().NotBeNull();
        capturedTrace!.DeliveryId.Should().Be("delivery-123");
        capturedTrace.Owner.Should().Be("octo-org");
        capturedTrace.Repo.Should().Be("reviewbot");
        capturedTrace.PrNumber.Should().Be(42);
        capturedTrace.ModelProvider.Should().Be(ReviewConfig.Default.Model.Provider);
        capturedTrace.ModelName.Should().Be(ReviewConfig.Default.Model.Name);
        capturedTrace.FilesReviewed.Should().Equal("src/A.cs", "src/B.cs");
        capturedTrace.ChunkCount.Should().Be(1);
        capturedTrace.ReviewType.Should().Be("first_review");
        capturedTrace.FinalComments.Should().ContainSingle()
            .Which.Path.Should().Be("src/A.cs");
    }

    [Fact]
    public async Task TraceRecordsRawCandidateCommentsAndDropReasons()
    {
        ReviewTrace? capturedTrace = null;
        var traceWriter = Substitute.For<IReviewTraceWriter>();
        traceWriter.WriteAsync(Arg.Any<ReviewTrace>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedTrace = call.Arg<ReviewTrace>();
                return Task.CompletedTask;
            });

        await using var fixture = new WorkerFixture(traceWriter: traceWriter);
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { MinConfidence = Confidence.High }
        };
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Found one issue.",
                [
                    new InlineComment("src/App.cs", 1, "RIGHT", "This should handle null input before dereferencing the value.", Severity.Warning, Confidence.High),
                    new InlineComment("src/App.cs", 2, "RIGHT", "Consider extracting this into a helper.", Severity.Info, Confidence.Medium),
                    new InlineComment("src/App.cs", 3, "RIGHT", "Nice guard.", Severity.Info, Confidence.High)
                ]));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        capturedTrace.Should().NotBeNull();
        capturedTrace!.CandidateComments.Select(c => c.Body)
            .Should().Equal(
                "This should handle null input before dereferencing the value.",
                "Consider extracting this into a helper.",
                "Nice guard.");
        capturedTrace.FinalComments.Should().ContainSingle()
            .Which.Body.Should().Be("This should handle null input before dereferencing the value.");
        capturedTrace.DroppedComments.Select(c => (c.Body, c.Reason))
            .Should().BeEquivalentTo(
            [
                ("Consider extracting this into a helper.", "below_min_confidence"),
                ("Nice guard.", "praise_only")
            ]);
    }

    [Fact]
    public async Task TraceContainsChunkDataAndTimingsAfterReview()
    {
        ReviewTrace? capturedTrace = null;
        var traceWriter = Substitute.For<IReviewTraceWriter>();
        traceWriter.IncludePrompts.Returns(true);
        traceWriter.WriteAsync(Arg.Any<ReviewTrace>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedTrace = call.Arg<ReviewTrace>();
                return Task.CompletedTask;
            });

        await using var fixture = new WorkerFixture(traceWriter: traceWriter);
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/A.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("Looks fine.", []) { RawLlmResponse = "{\"summary\":\"ok\",\"comments\":[]}" });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        capturedTrace.Should().NotBeNull();
        capturedTrace!.ChunkTraces.Should().NotBeNull().And.HaveCount(1);
        var chunk = capturedTrace.ChunkTraces![0];
        chunk.ChunkIndex.Should().Be(1);
        chunk.TotalChunks.Should().Be(1);
        chunk.ElapsedMs.Should().BeGreaterThan(0);
        chunk.PromptSystem.Should().NotBeNullOrEmpty();
        chunk.PromptUser.Should().NotBeNullOrEmpty();
        chunk.PromptSystemBytes.Should().BeGreaterThan(0);
        chunk.RawLlmResponse.Should().Be("{\"summary\":\"ok\",\"comments\":[]}");

        capturedTrace.Timings.Should().NotBeNull();
        capturedTrace.Timings!.TotalMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TraceContainsAgenticContextRequestsFetchesAndDrops()
    {
        ReviewTrace? capturedTrace = null;
        var traceWriter = Substitute.For<IReviewTraceWriter>();
        traceWriter.WriteAsync(Arg.Any<ReviewTrace>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedTrace = call.Arg<ReviewTrace>();
                return Task.CompletedTask;
            });

        await using var fixture = new WorkerFixture(traceWriter: traceWriter);
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with
            {
                AgenticContext = true,
                MaxContextRequests = 2
            },
            Ignore = ["docs/**"]
        };
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/App.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult(
                "Initial.",
                [new InlineComment("src/App.cs", 1, "RIGHT", "Initial comment.", Severity.Info)],
                [
                    new ContextRequest("../bad.cs", "unsafe"),
                    new ContextRequest("docs/Guide.md", "ignored docs"),
                    new ContextRequest("src/IFoo.cs", "contract"),
                    new ContextRequest("src/Base.cs", "base class"),
                    new ContextRequest("src/TooMany.cs", "over cap")
                ])
            {
                TokenUsage = new LlmTokenUsage(1000, 200)
            });
        fixture.PullRequestFetcher.GetFileContentsAsync(default!, default!, default!, default!, default, default!, default)
            .ReturnsForAnyArgs([("src/IFoo.cs", "public interface IFoo {}")]);
        fixture.Llm.CompleteRawAsync(Arg.Any<PromptPayload>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns("""
            {
              "summary": "Final summary.",
              "comments": [
                {
                  "path": "src/App.cs",
                  "line": 1,
                  "side": "RIGHT",
                  "severity": "warning",
                  "confidence": "high",
                  "body": "Final context-informed comment."
                }
              ]
            }
            """);
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        capturedTrace.Should().NotBeNull();
        var chunk = capturedTrace!.ChunkTraces.Should().ContainSingle().Subject;
        chunk.AgenticContext.Should().NotBeNull();
        var agentic = chunk.AgenticContext!;
        agentic.SecondPassRan.Should().BeTrue();
        agentic.Requested.Select(request => request.Path)
            .Should().Equal("../bad.cs", "docs/Guide.md", "src/IFoo.cs", "src/Base.cs", "src/TooMany.cs");
        agentic.Accepted.Select(request => request.Path)
            .Should().Equal("src/IFoo.cs", "src/Base.cs");
        agentic.FetchedPaths.Should().Equal("src/IFoo.cs");
        agentic.DropCounts.Should().BeEquivalentTo(
        [
            new TraceDropCount { Reason = "invalid_path", Count = 1 },
            new TraceDropCount { Reason = "ignored", Count = 1 },
            new TraceDropCount { Reason = "cap", Count = 1 }
        ]);
        capturedTrace.TokenUsage.Should().BeEquivalentTo(new TraceLlmTokenUsage
        {
            PromptTokens = 1000,
            CompletionTokens = 200,
            CachedPromptTokens = 0
        });
    }

    [Fact]
    public async Task TraceContainsEstimatedCostWhenCostCalculatorReturnsValue()
    {
        ReviewTrace? capturedTrace = null;
        var traceWriter = Substitute.For<IReviewTraceWriter>();
        traceWriter.WriteAsync(Arg.Any<ReviewTrace>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedTrace = call.Arg<ReviewTrace>();
                return Task.CompletedTask;
            });

        var costCalculator = Substitute.For<IReviewCostCalculator>();
        costCalculator.ComputeCostUsd(Arg.Any<string>(), Arg.Any<LlmTokenUsage>())
            .Returns(0.042m);

        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new WorkerFixture(traceWriter: traceWriter, costCalculator: costCalculator);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/A.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("Looks fine.", []) { TokenUsage = new LlmTokenUsage(1000, 200) });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        capturedTrace.Should().NotBeNull();
        capturedTrace!.EstimatedCostUsd.Should().Be(0.042m);
        costCalculator.Received(1).ComputeCostUsd(ReviewConfig.Default.Model.Name, Arg.Any<LlmTokenUsage>());
    }

    [Fact]
    public async Task TraceHasNullEstimatedCostWhenCostCalculatorReturnsNull()
    {
        ReviewTrace? capturedTrace = null;
        var traceWriter = Substitute.For<IReviewTraceWriter>();
        traceWriter.WriteAsync(Arg.Any<ReviewTrace>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedTrace = call.Arg<ReviewTrace>();
                return Task.CompletedTask;
            });

        var costCalculator = Substitute.For<IReviewCostCalculator>();
        costCalculator.ComputeCostUsd(Arg.Any<string>(), Arg.Any<LlmTokenUsage>())
            .Returns((decimal?)null);

        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new WorkerFixture(traceWriter: traceWriter, costCalculator: costCalculator);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/A.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("Looks fine.", []) { TokenUsage = new LlmTokenUsage(1000, 200) });
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        capturedTrace.Should().NotBeNull();
        capturedTrace!.EstimatedCostUsd.Should().BeNull();
    }

    [Fact]
    public async Task OtelSpansAreEmittedForReview()
    {
        var activities = new List<DiagActivity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ReviewBotActivitySource.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (activities) activities.Add(a); }
        };
        ActivitySource.AddActivityListener(listener);

        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new WorkerFixture();

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchFilesAsync(default!, default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs([CreateFile("src/A.cs")]);
        fixture.Llm.ReviewAsync(Arg.Any<ReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("Looks fine.", []));
        fixture.ReviewPoster.PostAsync(default!, default!, default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                posted.SetResult();
                return Task.CompletedTask;
            });

        await fixture.StartAsync();
        await fixture.Queue.EnqueueAsync(CreateJob(), CancellationToken.None);
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        List<DiagActivity> snapshot;
        lock (activities) snapshot = [..activities];

        var reviewSpan = snapshot.Should().ContainSingle(a => a.OperationName == "reviewbot.review").Subject;
        reviewSpan.GetTagItem("review.owner").Should().Be("octo-org");
        reviewSpan.GetTagItem("review.repo").Should().Be("reviewbot");
        reviewSpan.GetTagItem("review.pr_number").Should().Be(42);
        reviewSpan.GetTagItem("review.sha").Should().Be("snapshot-head");
        reviewSpan.GetTagItem("review.model").Should().NotBeNull();

        snapshot.Should().Contain(a => a.OperationName == "reviewbot.grounding");
        snapshot.Should().Contain(a => a.OperationName == "reviewbot.retrieval");
        snapshot.Should().Contain(a => a.OperationName == "reviewbot.chunk_review");
        snapshot.Should().Contain(a => a.OperationName == "reviewbot.llm.review");
        snapshot.Should().Contain(a => a.OperationName == "reviewbot.post_review");
    }

    private static ReviewJob CreateJob(string deliveryId = "delivery-123", string reason = "review_requested")
    {
        return new ReviewJob(
            DeliveryId: deliveryId,
            InstallationId: 98765,
            Owner: "octo-org",
            Repo: "reviewbot",
            PrNumber: 42,
            HeadSha: "event-head",
            Reason: reason);
    }

    private static PullRequestMetadata CreateMetadata() =>
        new PullRequestMetadata("Improve parser", "Adds coverage.", "base-sha", "snapshot-head");

    private static FileChange CreateFile(string path, long? additionsCount = null, long? deletionsCount = null)
    {
        return CreateFile(
            path,
            patch: "@@ -1,2 +1,2 @@\n line\n+added",
            commentableLines: new HashSet<int> { 1, 2 },
            additionsCount: additionsCount,
            deletionsCount: deletionsCount);
    }

    private static FileChange CreateFile(string path, int patchLines)
    {
        var patch = string.Join('\n', Enumerable.Range(1, patchLines).Select(line => $"+line {line}"));
        var commentableLines = Enumerable.Range(1, Math.Max(1, patchLines)).ToHashSet();

        return CreateFile(path, patch, commentableLines);
    }

    private static FileChange CreateFile(
        string path,
        string patch,
        IReadOnlySet<int> commentableLines,
        FileChangeStatus status = FileChangeStatus.Modified,
        long? additionsCount = null,
        long? deletionsCount = null)
    {
        return new FileChange(
            path,
            patch,
            commentableLines,
            AdditionsCount: additionsCount ?? (status == FileChangeStatus.Removed ? 0 : 1),
            DeletionsCount: deletionsCount ?? (status == FileChangeStatus.Added ? 0 : 1),
            status);
    }

    private sealed class FixedModelContextRegistry(int contextWindowTokens) : IModelContextRegistry
    {
        public int GetContextWindowTokens(string modelIdentifier) => contextWindowTokens;
    }

    private sealed class KeywordTokenEstimator(IReadOnlyDictionary<string, int> tokenMap) : IPromptTokenEstimator
    {
        public int EstimateTokens(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            foreach (var pair in tokenMap)
            {
                if (text.Contains(pair.Key, StringComparison.Ordinal))
                {
                    return pair.Value;
                }
            }

            return 0;
        }
    }

    private sealed class ProviderKeywordTokenEstimator(
        string providerName,
        IReadOnlyDictionary<string, int> tokenMap) : IReviewPromptTokenEstimator
    {
        public List<string> SeenProviders { get; } = [];

        public int EstimateTokens(ModelConfig model, string? text)
        {
            SeenProviders.Add(model.Provider);
            if (!string.Equals(model.Provider, providerName, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(text))
            {
                return 0;
            }

            foreach (var pair in tokenMap)
            {
                if (text.Contains(pair.Key, StringComparison.Ordinal))
                {
                    return pair.Value;
                }
            }

            return 0;
        }
    }

    private sealed class TestWorkspace(string localPath) : IWorkspace
    {
        public string LocalPath { get; } = localPath;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WorkerFixture : IAsyncDisposable
    {
        private readonly ReviewWorker worker;

        public WorkerFixture(
            int concurrency = 1,
            ILogger<ReviewWorker>? logger = null,
            IModelContextRegistry? modelContextRegistry = null,
            IPromptTokenEstimator? tokenEstimator = null,
            IReviewPromptTokenEstimator? reviewTokenEstimator = null,
            IReviewTraceWriter? traceWriter = null,
            IReviewCostCalculator? costCalculator = null)
        {
            Queue = new ChannelReviewJobQueue();
            TokenProvider = Substitute.For<IInstallationTokenProvider>();
            PullRequestFetcher = Substitute.For<IPullRequestFetcher>();
            RepoConfigFetcher = Substitute.For<IRepoConfigFetcher>();
            LlmFactory = Substitute.For<IReviewLlmFactory>();
            Llm = Substitute.For<IReviewLlm>();
            ReviewPoster = Substitute.For<IReviewPoster>();
            GroundingProvider = Substitute.For<IGroundingProvider>();
            PrReviewStateStore = Substitute.For<IPrReviewStateStore>();
            Metrics = new ReviewBotMetrics();
            ModelContextRegistry = modelContextRegistry ?? Substitute.For<IModelContextRegistry>();
            TokenEstimator = tokenEstimator ?? new HeuristicTokenEstimator();
            ReviewTokenEstimator = reviewTokenEstimator ?? new ReviewPromptTokenEstimator(TokenEstimator, []);
            RetrievalProvider = Substitute.For<IRetrievalProvider>();
            RepoIndexFactory = Substitute.For<IRepoIndexFactory>();
            RepoIndex = Substitute.For<IRepoIndex>();
            WorkspaceFactory = Substitute.For<IWorkspaceFactory>();
            CostCalculator = costCalculator ?? Substitute.For<IReviewCostCalculator>();

            TokenProvider.GetTokenAsync(98765, Arg.Any<CancellationToken>())
                .Returns(new InstallationToken("install-token", DateTimeOffset.UtcNow.AddHours(1)));
            if (modelContextRegistry is null)
            {
                ModelContextRegistry.GetContextWindowTokens(Arg.Any<string>())
                    .Returns(call => new ModelContextRegistry().GetContextWindowTokens(call.Arg<string>()));
            }
            LlmFactory.Create(Arg.Any<ModelConfig>()).Returns(Llm);
            RepoIndexFactory.Create(Arg.Any<string>()).Returns(RepoIndex);
            RetrievalProvider.GetContextAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<ReviewRequest>(),
                    Arg.Any<PromptBudget>(),
                    Arg.Any<CancellationToken>())
                .Returns(call => new RetrievalContextResult([], call.Arg<PromptBudget>()));
            GroundingProvider.GetContextAsync(Arg.Any<GroundingRequest>(), Arg.Any<CancellationToken>())
                .Returns(new GroundingContext(null, null, null));
            // Default: no prior review recorded (first review)
            PrReviewStateStore.GetLastShaAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns((string?)null);
            // Default metadata returned by FetchMetadataAsync
            PullRequestFetcher.FetchMetadataAsync(default!, default!, default, default!, default)
                .ReturnsForAnyArgs(CreateMetadata());

            worker = new ReviewWorker(
                Queue,
                TokenProvider,
                PullRequestFetcher,
                RepoConfigFetcher,
                LlmFactory,
                ReviewPoster,
                GroundingProvider,
                PrReviewStateStore,
                Metrics,
                ModelContextRegistry,
                ReviewTokenEstimator,
                RetrievalProvider,
                RepoIndexFactory,
                WorkspaceFactory,
                CostCalculator,
                traceWriter ?? NullReviewTraceWriter.Instance,
                TimeProvider.System,
                Microsoft.Extensions.Options.Options.Create(new WorkerOptions { Concurrency = concurrency }),
                logger ?? NullLogger<ReviewWorker>.Instance);
        }

        public ChannelReviewJobQueue Queue { get; }

        public IInstallationTokenProvider TokenProvider { get; }

        public IPullRequestFetcher PullRequestFetcher { get; }

        public IRepoConfigFetcher RepoConfigFetcher { get; }

        public IReviewLlmFactory LlmFactory { get; }

        public IReviewLlm Llm { get; }

        public IReviewPoster ReviewPoster { get; }

        public IGroundingProvider GroundingProvider { get; }

        public IPrReviewStateStore PrReviewStateStore { get; }

        public ReviewBotMetrics Metrics { get; }

        public IModelContextRegistry ModelContextRegistry { get; }

        public IPromptTokenEstimator TokenEstimator { get; }

        public IReviewPromptTokenEstimator ReviewTokenEstimator { get; }

        public IRetrievalProvider RetrievalProvider { get; }

        public IRepoIndexFactory RepoIndexFactory { get; }

        public IRepoIndex RepoIndex { get; }

        public IWorkspaceFactory WorkspaceFactory { get; }

        public IReviewCostCalculator CostCalculator { get; }

        public async Task StartAsync()
        {
            await worker.StartAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
            Metrics.Dispose();
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public TaskCompletionSource<(LogLevel Level, string Message, Exception? Exception)> WarningLogged { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<(LogLevel Level, string Message, Exception? Exception)> ErrorLogged { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull =>
            NullLogger.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (logLevel == LogLevel.Warning)
            {
                WarningLogged.TrySetResult((logLevel, message, exception));
            }
            else if (logLevel == LogLevel.Error)
            {
                ErrorLogged.TrySetResult((logLevel, message, exception));
            }
        }
    }
}
