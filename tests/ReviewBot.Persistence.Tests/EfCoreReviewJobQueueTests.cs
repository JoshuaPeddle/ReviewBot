using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReviewBot.Core.Jobs;
using ReviewBot.Persistence.Entities;

namespace ReviewBot.Persistence.Tests;

public class EfCoreReviewJobQueueTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EnqueuedJobSurvivesQueueRecreationAndCompletes()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        var firstQueue = fixture.CreateQueue();
        var job = CreateJob("delivery-1");

        var inserted = await firstQueue.TryEnqueueAsync(job, CancellationToken.None);
        var recreatedQueue = fixture.CreateQueue();
        await using var reader = recreatedQueue.DequeueAllAsync(CancellationToken.None).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        (await reader.MoveNextAsync()).Should().BeTrue();
        reader.Current.LeaseToken.Should().NotBeNullOrWhiteSpace();
        (reader.Current with { LeaseToken = null }).Should().Be(job);
        (await recreatedQueue.CompleteAsync(reader.Current, CancellationToken.None)).Should().BeTrue();

        await using var db = fixture.CreateContext();
        var record = await db.ReviewJobs.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        inserted.Should().BeTrue();
        record.Status.Should().Be("completed");
        record.CompletedAt.Should().Be(StartTime);
    }

    [Fact]
    public async Task DuplicateAutomaticReviewForSameHeadIsCoalesced()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        var queue = fixture.CreateQueue();

        var first = await queue.TryEnqueueAsync(CreateJob("delivery-1"), CancellationToken.None);
        var duplicate = await queue.TryEnqueueAsync(CreateJob("delivery-2"), CancellationToken.None);

        first.Should().BeTrue();
        duplicate.Should().BeFalse();
        await using var db = fixture.CreateContext();
        (await db.ReviewJobs.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task ReviewRequestIsNotSwallowedByAnActiveSynchronizeForTheSameHead()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        var queue = fixture.CreateQueue();

        var synchronize = await queue.TryEnqueueAsync(
            CreateJob("delivery-sync", reason: "synchronize"),
            CancellationToken.None);
        var requested = await queue.TryEnqueueAsync(
            CreateJob("delivery-requested", reason: "review_requested"),
            CancellationToken.None);

        synchronize.Should().BeTrue();
        requested.Should().BeTrue(
            "repository policy may skip the synchronize trigger while allowing explicit review requests");
        await using var db = fixture.CreateContext();
        (await db.ReviewJobs.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken))
            .Select(record => record.Reason)
            .Should().BeEquivalentTo("synchronize", "review_requested");
    }

    [Theory]
    [InlineData("synchronize")]
    [InlineData("review_requested")]
    [InlineData("opened")]
    [InlineData("reopened")]
    public async Task DuplicateAutomaticReviewWithTheSameReasonAndHeadIsCoalesced(string reason)
    {
        await using var fixture = await QueueFixture.CreateAsync();
        var queue = fixture.CreateQueue();

        var first = await queue.TryEnqueueAsync(
            CreateJob("delivery-first", reason: reason),
            CancellationToken.None);
        var duplicate = await queue.TryEnqueueAsync(
            CreateJob("delivery-duplicate", reason: reason),
            CancellationToken.None);

        first.Should().BeTrue();
        duplicate.Should().BeFalse();
        await using var db = fixture.CreateContext();
        (await db.ReviewJobs.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task CompletedReviewDoesNotSuppressANewDeliveryForTheSameHead()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        var queue = fixture.CreateQueue();
        var first = CreateJob("delivery-1");
        await queue.TryEnqueueAsync(first, CancellationToken.None);
        await using (var reader = queue.DequeueAllAsync(CancellationToken.None).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            (await reader.MoveNextAsync()).Should().BeTrue();
            await queue.CompleteAsync(reader.Current, CancellationToken.None);
        }

        var rereview = await queue.TryEnqueueAsync(
            CreateJob("delivery-2", reason: "review_requested"),
            CancellationToken.None);

        rereview.Should().BeTrue();
        await using var db = fixture.CreateContext();
        (await db.ReviewJobs.CountAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().Be(2);
    }

    [Fact]
    public async Task OutOfOrderOlderDeliveryCannotSupersedeTheQueuedCurrentHead()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        var queue = fixture.CreateQueue();

        await queue.TryEnqueueAsync(
            CreateJob("delivery-current") with { HeadSha = "new-head" },
            CancellationToken.None);
        await queue.TryEnqueueAsync(
            CreateJob("delivery-late-old") with { HeadSha = "old-head" },
            CancellationToken.None);

        await using var db = fixture.CreateContext();
        var records = await db.ReviewJobs.OrderBy(record => record.DeliveryId).ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken);
        records.Should().HaveCount(2);
        records.Should().OnlyContain(record => record.Status == "queued");
        records.Select(record => record.HeadSha).Should().BeEquivalentTo("new-head", "old-head");
    }

    [Fact]
    public async Task ReviewCommentsAreRateLimitedForOneMinute()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        var queue = fixture.CreateQueue();

        var first = await queue.TryEnqueueAsync(
            CreateJob("delivery-1", reason: "review_comment", headSha: null),
            CancellationToken.None);
        var limited = await queue.TryEnqueueAsync(
            CreateJob("delivery-2", reason: "review_comment", headSha: null),
            CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        var afterCooldown = await queue.TryEnqueueAsync(
            CreateJob("delivery-3", reason: "review_comment", headSha: null),
            CancellationToken.None);

        first.Should().BeTrue();
        limited.Should().BeFalse();
        afterCooldown.Should().BeTrue();
    }

    [Fact]
    public async Task FailedJobIsRetriedAndEventuallyDeadLettered()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        var queue = fixture.CreateQueue();
        var job = CreateJob("delivery-retry");
        await queue.TryEnqueueAsync(job, CancellationToken.None);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await using var reader = queue.DequeueAllAsync(CancellationToken.None).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            (await reader.MoveNextAsync()).Should().BeTrue();
            await queue.FailAsync(reader.Current, $"failure {attempt}", CancellationToken.None);

            if (attempt == 1)
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(5));
            }
            else if (attempt == 2)
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(30));
            }
        }

        await using var db = fixture.CreateContext();
        var record = await db.ReviewJobs.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        record.Status.Should().Be("dead_letter");
        record.AttemptCount.Should().Be(3);
        record.LastError.Should().Be("failure 3");
    }

    [Fact]
    public async Task RepeatedlyExpiredLeaseIsDeadLetteredOnceAttemptsAreExhausted()
    {
        // A worker killed mid-review (OOM, SIGKILL, eviction) never reaches FailAsync, so
        // lease recovery is the only place its attempt is ever accounted for. Without the
        // bound, a job that reliably kills its worker is reclaimed forever. The fourth
        // claim is the recovery pass reserved for finalizing external status
        // (see ExpiredFinalAttemptIsRetriedSoAWorkerCanFinalizeExternalStatus); an expiry
        // after that one is terminal.
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await QueueFixture.CreateAsync();
        var job = CreateJob("delivery-abandoned");
        await fixture.CreateQueue().TryEnqueueAsync(job, CancellationToken.None);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var queue = fixture.CreateQueue();
            await using (var reader = queue.DequeueAllAsync(CancellationToken.None).GetAsyncEnumerator(ct))
            {
                (await reader.MoveNextAsync()).Should().BeTrue($"attempt {attempt} must be claimable");
                reader.Current.DeliveryId.Should().Be(job.DeliveryId);
            }

            fixture.Clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        }

        using var exhaustedCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var finalQueue = fixture.CreateQueue();
        await using (var finalReader = finalQueue
            .DequeueAllAsync(exhaustedCts.Token)
            .GetAsyncEnumerator(exhaustedCts.Token))
        {
            var move = finalReader.MoveNextAsync().AsTask();
            await move.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
        }

        await using var db = fixture.CreateContext();
        var record = await db.ReviewJobs.SingleAsync(ct);
        record.Status.Should().Be("dead_letter");
        record.AttemptCount.Should().Be(4, "claims are capped at MaxAttempts plus the finalization pass");
        record.LeaseToken.Should().BeNull();
        record.CompletedAt.Should().NotBeNull();
        record.LastError.Should().Contain("lease expired");
    }

    [Fact]
    public async Task ReleasingAnOwnedJobRequeuesItWithoutChargingAnAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await QueueFixture.CreateAsync();
        var queue = fixture.CreateQueue();
        await queue.TryEnqueueAsync(CreateJob("delivery-released"), CancellationToken.None);

        ReviewJob claimed;
        await using (var reader = queue.DequeueAllAsync(CancellationToken.None).GetAsyncEnumerator(ct))
        {
            (await reader.MoveNextAsync()).Should().BeTrue();
            claimed = reader.Current;
        }

        (await queue.ReleaseAsync(claimed, CancellationToken.None)).Should().BeTrue();

        await using var db = fixture.CreateContext();
        var record = await db.ReviewJobs.SingleAsync(ct);
        record.Status.Should().Be("queued");
        record.AttemptCount.Should().Be(0, "a release must refund the attempt it returns");
        record.LeaseToken.Should().BeNull();
        record.LeaseExpiresAt.Should().BeNull();
        record.StartedAt.Should().BeNull();
    }

    [Fact]
    public async Task ReleasingWithAStaleLeaseIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await QueueFixture.CreateAsync();
        var queue = fixture.CreateQueue();
        await queue.TryEnqueueAsync(CreateJob("delivery-stale-release"), CancellationToken.None);

        ReviewJob claimed;
        await using (var reader = queue.DequeueAllAsync(CancellationToken.None).GetAsyncEnumerator(ct))
        {
            (await reader.MoveNextAsync()).Should().BeTrue();
            claimed = reader.Current;
        }

        var released = await queue
            .ReleaseAsync(claimed with { LeaseToken = "not-the-current-lease" }, CancellationToken.None);

        released.Should().BeFalse();
        await using var db = fixture.CreateContext();
        var record = await db.ReviewJobs.SingleAsync(ct);
        record.Status.Should().Be("running");
        record.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task MatchingManualRedeliveryRevivesADeadLetteredJob()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await QueueFixture.CreateAsync();
        var job = CreateJob("delivery-redelivered");
        await using (var db = fixture.CreateContext())
        {
            db.ReviewJobs.Add(CreateDeadLetterRecord(job));
            await db.SaveChangesAsync(ct);
        }

        var accepted = await fixture.CreateQueue().TryEnqueueAsync(job, ct);

        accepted.Should().BeTrue();
        await using var verification = fixture.CreateContext();
        var record = await verification.ReviewJobs.SingleAsync(ct);
        record.Status.Should().Be("queued");
        record.AttemptCount.Should().Be(0);
        record.CreatedAt.Should().Be(StartTime);
        record.AvailableAt.Should().Be(StartTime);
        record.StartedAt.Should().BeNull();
        record.CompletedAt.Should().BeNull();
        record.LeaseExpiresAt.Should().BeNull();
        record.LeaseToken.Should().BeNull();
        record.LastError.Should().BeNull();
    }

    [Fact]
    public async Task MatchingManualRedeliveryDoesNotReviveACompletedJob()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await QueueFixture.CreateAsync();
        var queue = fixture.CreateQueue();
        var job = CreateJob("delivery-completed");
        (await queue.TryEnqueueAsync(job, ct)).Should().BeTrue();
        await using (var reader = queue.DequeueAllAsync(ct).GetAsyncEnumerator(ct))
        {
            (await reader.MoveNextAsync()).Should().BeTrue();
            (await queue.CompleteAsync(reader.Current, ct)).Should().BeTrue();
        }

        var accepted = await queue.TryEnqueueAsync(job, ct);

        accepted.Should().BeFalse();
        await using var verification = fixture.CreateContext();
        var record = await verification.ReviewJobs.SingleAsync(ct);
        record.Status.Should().Be("completed");
        record.AttemptCount.Should().Be(1);
        record.CompletedAt.Should().Be(StartTime);
    }

    [Fact]
    public async Task SameDeliveryIdWithMismatchedIdentityDoesNotReviveADeadLetteredJob()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await QueueFixture.CreateAsync();
        var queue = fixture.CreateQueue();
        var original = CreateJob("delivery-mismatch");
        await using (var db = fixture.CreateContext())
        {
            db.ReviewJobs.Add(CreateDeadLetterRecord(original));
            await db.SaveChangesAsync(ct);
        }

        ReviewJob[] mismatches =
        [
            original with { InstallationId = 456 },
            original with { Owner = "another-owner" },
            original with { Repo = "another-repo" },
            original with { PrNumber = 43 },
            original with { HeadSha = "another-head" },
            original with { Reason = "review_requested" }
        ];

        foreach (var mismatch in mismatches)
        {
            (await queue.TryEnqueueAsync(mismatch, ct)).Should().BeFalse();
        }

        await using var verification = fixture.CreateContext();
        var record = await verification.ReviewJobs.SingleAsync(ct);
        record.Status.Should().Be("dead_letter");
        record.AttemptCount.Should().Be(3);
        record.LastError.Should().Be("terminal failure");
    }

    [Fact]
    public async Task UnexpiredRunningJobIsNotRecoveredByAnotherQueueInstance()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        var ownerQueue = fixture.CreateQueue();
        var competingQueue = fixture.CreateQueue();
        var job = CreateJob("delivery-owned");
        await ownerQueue.TryEnqueueAsync(job, CancellationToken.None);

        await using var ownerReader = ownerQueue.DequeueAllAsync(CancellationToken.None).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await ownerReader.MoveNextAsync()).Should().BeTrue();

        using var competingCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await using var competingReader = competingQueue
            .DequeueAllAsync(competingCts.Token)
            .GetAsyncEnumerator(competingCts.Token);
        var competingMove = competingReader.MoveNextAsync().AsTask();
        await competingMove.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();

        await using var db = fixture.CreateContext();
        var record = await db.ReviewJobs.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        record.Status.Should().Be("running");
        record.AttemptCount.Should().Be(1);
        record.LeaseToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ExpiredLeaseIsReclaimedAndOldOwnerCannotCompleteNewAttempt()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        var oldQueue = fixture.CreateQueue();
        var newQueue = fixture.CreateQueue();
        var job = CreateJob("delivery-expired");
        await oldQueue.TryEnqueueAsync(job, CancellationToken.None);

        await using var oldReader = oldQueue.DequeueAllAsync(CancellationToken.None).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await oldReader.MoveNextAsync()).Should().BeTrue();
        var oldClaim = oldReader.Current;
        fixture.Clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));

        await using var newReader = newQueue.DequeueAllAsync(CancellationToken.None).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await newReader.MoveNextAsync()).Should().BeTrue();
        (await oldQueue.CompleteAsync(oldClaim, CancellationToken.None)).Should().BeFalse();

        await using (var db = fixture.CreateContext())
        {
            var record = await db.ReviewJobs.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            record.Status.Should().Be("running");
            record.AttemptCount.Should().Be(2);
            record.LeaseToken.Should().NotBeNullOrWhiteSpace();
        }

        var disposition = await newQueue.FailAsync(newReader.Current, "second attempt failed", CancellationToken.None);
        disposition.Should().Be(ReviewJobFailureDisposition.RetryScheduled);
    }

    [Fact]
    public async Task ReclaimedLeaseOnSameQueueDoesNotLetOldClaimMutateSuccessorAttempt()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        var queue = fixture.CreateQueue();
        var job = CreateJob("delivery-same-queue-expiry");
        await queue.TryEnqueueAsync(job, CancellationToken.None);

        ReviewJob oldClaim;
        await using (var oldReader = queue.DequeueAllAsync(CancellationToken.None).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            (await oldReader.MoveNextAsync()).Should().BeTrue();
            oldClaim = oldReader.Current;
        }

        fixture.Clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        await using var successorReader = queue.DequeueAllAsync(CancellationToken.None).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await successorReader.MoveNextAsync()).Should().BeTrue();
        var successorClaim = successorReader.Current;

        successorClaim.LeaseToken.Should().NotBe(oldClaim.LeaseToken);
        (await queue.RenewLeaseAsync(oldClaim, CancellationToken.None)).Should().BeFalse();
        (await queue.CompleteAsync(oldClaim, CancellationToken.None)).Should().BeFalse();
        (await queue.CompleteAsync(successorClaim, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task RenewedLeaseCannotBeReclaimedAtItsOriginalExpiry()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        var queue = fixture.CreateQueue();
        var job = CreateJob("delivery-renewed");
        await queue.TryEnqueueAsync(job, CancellationToken.None);

        await using var reader = queue.DequeueAllAsync(CancellationToken.None).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await reader.MoveNextAsync()).Should().BeTrue();
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));

        (await queue.RenewLeaseAsync(reader.Current, CancellationToken.None)).Should().BeTrue();

        await using var db = fixture.CreateContext();
        var record = await db.ReviewJobs.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        record.LeaseExpiresAt.Should().Be(StartTime + TimeSpan.FromMinutes(25));
    }



    [Fact]
    public async Task ExpiredFinalAttemptIsRetriedSoAWorkerCanFinalizeExternalStatus()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        var queue = fixture.CreateQueue();
        var job = CreateJob("delivery-final-expiry");
        await queue.TryEnqueueAsync(job, CancellationToken.None);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await using var reader = queue.DequeueAllAsync(CancellationToken.None).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            (await reader.MoveNextAsync()).Should().BeTrue();
            if (attempt < 3)
            {
                await queue.FailAsync(reader.Current, $"failure {attempt}", CancellationToken.None);
                fixture.Clock.Advance(attempt == 1 ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30));
            }
        }

        fixture.Clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        var recoveringQueue = fixture.CreateQueue();
        await using var recoveringReader = recoveringQueue
            .DequeueAllAsync(CancellationToken.None)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        (await recoveringReader.MoveNextAsync()).Should().BeTrue();
        (recoveringReader.Current with { LeaseToken = null }).Should().Be(job);
        (await recoveringQueue.FailAsync(recoveringReader.Current, "finalizer failed", CancellationToken.None))
            .Should().Be(ReviewJobFailureDisposition.DeadLettered);

        await using var db = fixture.CreateContext();
        var record = await db.ReviewJobs.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        record.Status.Should().Be("dead_letter");
        record.AttemptCount.Should().Be(4);
    }

    [Fact]
    public async Task PollDeletesOnlyTerminalJobsOlderThanThirtyDays()
    {
        await using var fixture = await QueueFixture.CreateAsync();
        await using (var db = fixture.CreateContext())
        {
            db.ReviewJobs.AddRange(
                CreateRecord("old-completed", "completed", StartTime - TimeSpan.FromDays(31), 1),
                CreateRecord("old-dead", "dead_letter", StartTime - TimeSpan.FromDays(31), 2),
                CreateRecord("recent-completed", "completed", StartTime - TimeSpan.FromDays(29), 3),
                CreateRecord("old-retry", "retry", StartTime - TimeSpan.FromDays(31), 4));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var queue = fixture.CreateQueue();
        await queue.TryEnqueueAsync(
            new ReviewJob("cleanup-trigger", 123, "other", "repo", 99, "trigger-sha", "synchronize"),
            CancellationToken.None);
        await using var reader = queue.DequeueAllAsync(CancellationToken.None).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await reader.MoveNextAsync()).Should().BeTrue();

        await using var verification = fixture.CreateContext();
        var remaining = await verification.ReviewJobs
            .Select(record => record.DeliveryId)
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        remaining.Should().BeEquivalentTo("recent-completed", "old-retry", "cleanup-trigger");
    }

    [Fact]
    public async Task MigrationCreatesDurableReviewJobSchema()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<ReviewBotDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new ReviewBotDbContext(options))
        {
            await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        var factory = new TestDbContextFactory(options);
        var queue = new EfCoreReviewJobQueue(
            factory,
            new FakeTimeProvider(StartTime),
            NullLogger<EfCoreReviewJobQueue>.Instance);

        (await queue.TryEnqueueAsync(CreateJob("after-migration"), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public void ModelMatchesTheInitialMigration()
    {
        var options = new DbContextOptionsBuilder<ReviewBotDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var db = new ReviewBotDbContext(options);

        db.Database.HasPendingModelChanges().Should().BeFalse();
    }

    private static ReviewJob CreateJob(
        string deliveryId,
        string reason = "synchronize",
        string? headSha = "head-sha") =>
        new(deliveryId, 123, "octo-org", "reviewbot", 42, headSha, reason);

    private static ReviewJobRecord CreateRecord(
        string deliveryId,
        string status,
        DateTimeOffset completedAt,
        int prNumber) =>
        new()
        {
            DeliveryId = deliveryId,
            InstallationId = 123,
            Owner = "octo-org",
            Repo = "reviewbot",
            PrNumber = prNumber,
            HeadSha = $"sha-{prNumber}",
            Reason = "synchronize",
            Status = status,
            AttemptCount = 1,
            CreatedAt = completedAt - TimeSpan.FromHours(1),
            AvailableAt = completedAt - TimeSpan.FromHours(1),
            CompletedAt = completedAt
        };

    private static ReviewJobRecord CreateDeadLetterRecord(ReviewJob job) =>
        new()
        {
            DeliveryId = job.DeliveryId,
            InstallationId = job.InstallationId,
            Owner = job.Owner,
            Repo = job.Repo,
            PrNumber = job.PrNumber,
            HeadSha = job.HeadSha,
            Reason = job.Reason,
            Status = "dead_letter",
            AttemptCount = 3,
            CreatedAt = StartTime - TimeSpan.FromHours(1),
            AvailableAt = StartTime - TimeSpan.FromMinutes(30),
            StartedAt = StartTime - TimeSpan.FromMinutes(2),
            CompletedAt = StartTime - TimeSpan.FromMinutes(1),
            LeaseExpiresAt = StartTime + TimeSpan.FromMinutes(13),
            LeaseToken = "stale-lease",
            LastError = "terminal failure"
        };

    private sealed class QueueFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly DbContextOptions<ReviewBotDbContext> options;
        private readonly TestDbContextFactory factory;

        private QueueFixture(SqliteConnection connection)
        {
            this.connection = connection;
            options = new DbContextOptionsBuilder<ReviewBotDbContext>()
                .UseSqlite(connection)
                .Options;
            factory = new TestDbContextFactory(options);
            Clock = new FakeTimeProvider(StartTime);
        }

        public FakeTimeProvider Clock { get; }

        public static async Task<QueueFixture> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var fixture = new QueueFixture(connection);
            await using var db = fixture.CreateContext();
            await db.Database.EnsureCreatedAsync();
            return fixture;
        }

        public ReviewBotDbContext CreateContext() => new(options);

        public EfCoreReviewJobQueue CreateQueue() =>
            new(factory, Clock, NullLogger<EfCoreReviewJobQueue>.Instance);

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<ReviewBotDbContext> options)
        : IDbContextFactory<ReviewBotDbContext>
    {
        public ReviewBotDbContext CreateDbContext() => new(options);

        public Task<ReviewBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
