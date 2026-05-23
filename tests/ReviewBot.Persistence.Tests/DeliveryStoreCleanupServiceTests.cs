using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using ReviewBot.Core.Idempotency;
using ReviewBot.Persistence;

namespace ReviewBot.Persistence.Tests;

public class DeliveryStoreCleanupServiceTests
{
    [Fact]
    public async Task RunsCleanupEachHourWithThirtyDayCutoff()
    {
        var now = new DateTimeOffset(2026, 5, 23, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var store = Substitute.For<IDeliveryStore>();
        var called = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.CleanupAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                called.TrySetResult(call.Arg<DateTimeOffset>());
                return Task.CompletedTask;
            });
        var service = CreateService(store, clock);

        await service.StartAsync(CancellationToken.None);
        await WaitForTimerRegistrationAsync();
        clock.Advance(TimeSpan.FromHours(1));

        var cutoff = await called.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cutoff.Should().Be(now.AddHours(1).AddDays(-30));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ContinuesLoopWhenCleanupFails()
    {
        var now = new DateTimeOffset(2026, 5, 23, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var store = Substitute.For<IDeliveryStore>();
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        store.CleanupAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstAttempt.TrySetResult();
                    return Task.FromException(new InvalidOperationException("cleanup failed"));
                }

                secondAttempt.TrySetResult();
                return Task.CompletedTask;
            });
        var service = CreateService(store, clock);

        await service.StartAsync(CancellationToken.None);
        await WaitForTimerRegistrationAsync();
        clock.Advance(TimeSpan.FromHours(1));
        await firstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromHours(1));
        await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2));

        calls.Should().Be(2);
        await service.StopAsync(CancellationToken.None);
    }

    private static DeliveryStoreCleanupService CreateService(IDeliveryStore store, TimeProvider clock) =>
        new(store, clock, NullLogger<DeliveryStoreCleanupService>.Instance);

    private static async Task WaitForTimerRegistrationAsync()
    {
        await Task.Yield();
        await Task.Delay(25);
    }
}
