using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReviewBot.Api.Workers;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Jobs;
using ReviewBot.Core.Llm;
using ReviewBot.GitHub.Auth;
using ReviewBot.GitHub.Config;
using ReviewBot.GitHub.Pulls;

namespace ReviewBot.Api.Tests.Workers;

public class ReviewWorkerTests
{
    [Fact]
    public async Task ProcessesOneJobThroughTheReviewPipeline()
    {
        await using var fixture = new WorkerFixture();
        var snapshot = CreateSnapshot(CreateFile("src/B.cs"), CreateFile("src/A.cs"));
        var result = new ReviewResult(
            "Looks good.",
            [new InlineComment("src/A.cs", 2, "RIGHT", "Nice guard.", Severity.Info)]);
        ReviewRequest? capturedRequest = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync("octo-org", "reviewbot", "event-head", "install-token", Arg.Any<CancellationToken>())
            .Returns(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchAsync("octo-org", "reviewbot", 42, "install-token", 50, Arg.Any<CancellationToken>())
            .Returns(snapshot);
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
                result,
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
            .FetchAsync(default!, default!, default, default!, default, default);
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
            .FetchAsync(default!, default!, default, default!, default, default);
        fixture.LlmFactory.DidNotReceiveWithAnyArgs().Create(default!);
    }

    [Fact]
    public async Task IgnoreGlobsDropMatchingFilesBeforeLlmAndPost()
    {
        await using var fixture = new WorkerFixture();
        var config = ReviewConfig.Default with { Ignore = ["**/*.generated.cs", "docs/**"] };
        var snapshot = CreateSnapshot(
            CreateFile("src/App.cs"),
            CreateFile("src/App.generated.cs"),
            CreateFile("docs/readme.md"));
        ReviewRequest? capturedRequest = null;
        IReadOnlyList<FileChange>? postedFiles = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchAsync(default!, default!, default, default!, default, default)
            .ReturnsForAnyArgs(snapshot);
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
        var snapshot = CreateSnapshot(
            CreateFile("src/C.cs"),
            CreateFile("src/A.cs"),
            CreateFile("src/B.cs"));
        ReviewRequest? capturedRequest = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchAsync("octo-org", "reviewbot", 42, "install-token", 2, Arg.Any<CancellationToken>())
            .Returns(snapshot);
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
        var snapshot = CreateSnapshot(
            CreateFile("src/Large.cs", patchLines: 8),
            CreateFile("src/Small.cs", patchLines: 3),
            CreateFile("src/Medium.cs", patchLines: 5));
        ReviewRequest? capturedRequest = null;
        ReviewResult? postedResult = null;
        IReadOnlyList<FileChange>? postedFiles = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchAsync(default!, default!, default, default!, default, default)
            .ReturnsForAnyArgs(snapshot);
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
        var snapshot = CreateSnapshot(
            CreateFile("src/A.cs", patchLines: 4),
            CreateFile("src/B.cs", patchLines: 6));
        ReviewRequest? capturedRequest = null;
        ReviewResult? postedResult = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchAsync(default!, default!, default, default!, default, default)
            .ReturnsForAnyArgs(snapshot);
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
        var snapshot = CreateSnapshot(CreateFile("src/App.cs"));
        ReviewResult? postedResult = null;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(config);
        fixture.PullRequestFetcher.FetchAsync(default!, default!, default, default!, default, default)
            .ReturnsForAnyArgs(snapshot);
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
    public async Task JobExceptionIsLoggedAndDoesNotStopSubsequentJobs()
    {
        await using var fixture = new WorkerFixture();
        var snapshot = CreateSnapshot(CreateFile("src/App.cs"));
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetchCalls = 0;

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchAsync(default!, default!, default, default!, default, default)
            .ReturnsForAnyArgs(_ =>
            {
                fetchCalls++;
                return fetchCalls == 1
                    ? Task.FromException<PullRequestSnapshot>(new InvalidOperationException("fetch failed"))
                    : Task.FromResult(snapshot);
            });
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

        fetchCalls.Should().Be(2);
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
        var snapshot = CreateSnapshot(CreateFile("src/A.cs"));
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchAsync(default!, default!, default, default!, default, default)
            .ReturnsForAnyArgs(snapshot);
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
        fixture.PullRequestFetcher.FetchAsync(default!, default!, default, default!, default, default)
            .ReturnsForAnyArgs(CreateSnapshot(CreateFile("src/A.cs")));
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
        var snapshot = CreateSnapshot(CreateFile("src/A.cs"));
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.RepoConfigFetcher.FetchAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(ReviewConfig.Default);
        fixture.PullRequestFetcher.FetchAsync(default!, default!, default, default!, default, default)
            .ReturnsForAnyArgs(snapshot);
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

    private static PullRequestSnapshot CreateSnapshot(params FileChange[] files)
    {
        return new PullRequestSnapshot(
            Title: "Improve parser",
            Body: "Adds coverage.",
            BaseSha: "base-sha",
            HeadSha: "snapshot-head",
            Files: files);
    }

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

        public WorkerFixture()
        {
            Queue = new ChannelReviewJobQueue();
            TokenProvider = Substitute.For<IInstallationTokenProvider>();
            PullRequestFetcher = Substitute.For<IPullRequestFetcher>();
            RepoConfigFetcher = Substitute.For<IRepoConfigFetcher>();
            LlmFactory = Substitute.For<IReviewLlmFactory>();
            Llm = Substitute.For<IReviewLlm>();
            ReviewPoster = Substitute.For<IReviewPoster>();
            Metrics = new ReviewBotMetrics();

            TokenProvider.GetTokenAsync(98765, Arg.Any<CancellationToken>())
                .Returns(new InstallationToken("install-token", DateTimeOffset.UtcNow.AddHours(1)));
            LlmFactory.Create(Arg.Any<ModelConfig>()).Returns(Llm);

            worker = new ReviewWorker(
                Queue,
                TokenProvider,
                PullRequestFetcher,
                RepoConfigFetcher,
                LlmFactory,
                ReviewPoster,
                Metrics,
                NullLogger<ReviewWorker>.Instance);
        }

        public ChannelReviewJobQueue Queue { get; }

        public IInstallationTokenProvider TokenProvider { get; }

        public IPullRequestFetcher PullRequestFetcher { get; }

        public IRepoConfigFetcher RepoConfigFetcher { get; }

        public IReviewLlmFactory LlmFactory { get; }

        public IReviewLlm Llm { get; }

        public IReviewPoster ReviewPoster { get; }

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
