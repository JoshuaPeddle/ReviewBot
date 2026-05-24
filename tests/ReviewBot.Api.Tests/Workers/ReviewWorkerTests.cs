using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Octokit;
using ReviewBot.Api.Workers;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Jobs;
using ReviewBot.Core.Llm;
using ReviewBot.Core.Prompting;
using ReviewBot.Core.Storage;
using ReviewBot.GitHub.Auth;
using ReviewBot.GitHub.Config;
using ReviewBot.GitHub.Pulls;
using ReviewBot.Grounding;

namespace ReviewBot.Api.Tests.Workers;

public class ReviewWorkerTests
{
    [Fact]
    public async Task ProcessesOneJobThroughTheReviewPipeline()
    {
        await using var fixture = new WorkerFixture();
        var files = new[] { CreateFile("src/B.cs"), CreateFile("src/A.cs") };
        var result = new ReviewResult(
            "Looks good.",
            [new InlineComment("src/A.cs", 2, "RIGHT", "Nice guard.", Severity.Info)]);
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
    public async Task DisabledConfigShortCircuitsBeforeFetchingPullRequest()
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
            .FetchMetadataAsync(default!, default!, default, default!, default);
        fixture.LlmFactory.DidNotReceiveWithAnyArgs().Create(default!);
        await fixture.ReviewPoster.DidNotReceiveWithAnyArgs()
            .PostAsync(default!, default!, default, default!, default!, default!, default!, default);
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
            .FetchMetadataAsync(default!, default!, default, default!, default);
        fixture.LlmFactory.DidNotReceiveWithAnyArgs().Create(default!);
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
            Review = ReviewConfig.Default.Review with { MaxPatchLines = 2 }
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
            Review = ReviewConfig.Default.Review with { MaxPatchLines = 2 }
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
                "Looks good.",
                [new InlineComment("src/A.cs", 1, "RIGHT", "Nice.", Severity.Info)]));
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
            .Which.provider.Should().Be("anthropic");
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
            Review = ReviewConfig.Default.Review with { MaxPatchLines = 1 }
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
            .CompleteRawAsync(default!, default);
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
        fixture.Llm.CompleteRawAsync(Arg.Any<PromptPayload>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                enrichedPrompt = call.Arg<PromptPayload>();
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
            .CompleteRawAsync(default!, default);
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
            .CompleteRawAsync(default!, default);
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
        fixture.Llm.CompleteRawAsync(Arg.Any<PromptPayload>(), Arg.Any<CancellationToken>())
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
            .CompleteRawAsync(default!, default);
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
            .CompleteRawAsync(default!, default);
    }

    [Fact]
    public async Task SelfCritiqueRetainsSelectedLowerConfidenceCommentsAndAllHighConfidenceComments()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with
        {
            Review = ReviewConfig.Default.Review with { SelfCritique = true }
        };
        ReviewResult? postedResult = null;
        PromptPayload? critiquePrompt = null;
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
        fixture.Llm.CompleteRawAsync(Arg.Any<PromptPayload>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                critiquePrompt = call.Arg<PromptPayload>();
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

        postedResult!.Comments.Select(c => c.Body)
            .Should().Equal("High stays.", "Medium kept.", "Medium kept too.");
        critiquePrompt.Should().NotBeNull();
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
        fixture.Llm.CompleteRawAsync(Arg.Any<PromptPayload>(), Arg.Any<CancellationToken>())
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
            .CompleteRawAsync(default!, default);
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

    private static FileChange CreateFile(string path)
    {
        return CreateFile(
            path,
            patch: "@@ -1,2 +1,2 @@\n line\n+added",
            commentableLines: new HashSet<int> { 1, 2 });
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
        IReadOnlySet<int> commentableLines)
    {
        return new FileChange(
            path,
            patch,
            commentableLines,
            AdditionsCount: 1,
            DeletionsCount: 0,
            FileChangeStatus.Modified);
    }

    private sealed class WorkerFixture : IAsyncDisposable
    {
        private readonly ReviewWorker worker;

        public WorkerFixture(int concurrency = 1)
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

            TokenProvider.GetTokenAsync(98765, Arg.Any<CancellationToken>())
                .Returns(new InstallationToken("install-token", DateTimeOffset.UtcNow.AddHours(1)));
            LlmFactory.Create(Arg.Any<ModelConfig>()).Returns(Llm);
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
                Microsoft.Extensions.Options.Options.Create(new WorkerOptions { Concurrency = concurrency }),
                NullLogger<ReviewWorker>.Instance);
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
}
