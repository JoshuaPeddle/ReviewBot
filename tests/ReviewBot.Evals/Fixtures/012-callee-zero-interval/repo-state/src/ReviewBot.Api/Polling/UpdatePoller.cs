using Microsoft.Extensions.Hosting;
using ReviewBot.Core.Scheduling;

namespace ReviewBot.Api.Polling;

public sealed class UpdatePoller : IHostedService
{
    private readonly PollScheduler scheduler;

    public UpdatePoller(PollScheduler scheduler)
    {
        this.scheduler = scheduler;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Kick off the first poll right away instead of waiting a full interval.
        scheduler.Start(0);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        scheduler.Stop();
        return Task.CompletedTask;
    }
}
