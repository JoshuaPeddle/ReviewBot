namespace ReviewBot.Core.Scheduling;

public sealed class PollScheduler : IDisposable
{
    private readonly Func<CancellationToken, Task> poll;
    private CancellationTokenSource? loopCts;

    public PollScheduler(Func<CancellationToken, Task> poll)
    {
        this.poll = poll;
    }

    public void Start(int intervalSeconds)
    {
        if (intervalSeconds <= 0)
        {
            // A non-positive interval means polling is disabled for this host.
            return;
        }

        loopCts = new CancellationTokenSource();
        _ = RunLoopAsync(TimeSpan.FromSeconds(intervalSeconds), loopCts.Token);
    }

    public void Stop() => loopCts?.Cancel();

    public void Dispose() => loopCts?.Dispose();

    private async Task RunLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await poll(ct).ConfigureAwait(false);
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }
}
