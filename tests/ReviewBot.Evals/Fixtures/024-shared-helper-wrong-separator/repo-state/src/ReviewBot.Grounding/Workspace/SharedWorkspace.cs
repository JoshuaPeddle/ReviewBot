using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReviewBot.Grounding.Workspace;

internal sealed class SharedWorkspace : ISharedWorkspace
{
    private readonly IWorkspaceFactory factory;
    private readonly ILogger logger;
    private readonly object gate = new();
    private readonly Dictionary<(string CloneUrl, string Sha), Task<IWorkspace>> clones = new();
    private readonly CancellationTokenSource scopeCts = new();
    private bool disposed;

    public SharedWorkspace(IWorkspaceFactory factory, ILogger? logger = null)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.logger = logger ?? NullLogger.Instance;
    }

    public async Task<IWorkspace> GetOrCreateAsync(WorkspaceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = (request.CloneUrl, request.Sha);

        Task<IWorkspace> cloneTask;
        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            if (!this.clones.TryGetValue(key, out cloneTask!))
            {
                // Bind the clone to the scope's token, not the caller's: one consumer
                // cancelling must not tear down a clone other consumers still share
                // (per-consumer cancellation is honoured by the WaitAsync below). The
                // scope token is cancelled on DisposeAsync so an in-flight clone is
                // torn down at job end instead of leaking its git process.
                cloneTask = this.factory.CreateAsync(request, this.scopeCts.Token);
                this.clones[key] = cloneTask;
            }
        }

        try
        {
            return await cloneTask.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed clone (e.g. transient network error) should not be cached as
            // a permanent failure for later consumers; evict so the next caller can
            // retry. Only evict the exact task we observed in case it was replaced.
            lock (this.gate)
            {
                if (this.clones.TryGetValue(key, out var current) && ReferenceEquals(current, cloneTask))
                {
                    this.clones.Remove(key);
                }
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Task<IWorkspace>> pending;
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            pending = this.clones.Values.ToList();
            this.clones.Clear();
        }

        // Cancel before awaiting so an in-flight clone is torn down (its git process
        // killed and temp dir cleaned by the factory) rather than blocking disposal.
        await this.scopeCts.CancelAsync().ConfigureAwait(false);

        foreach (var cloneTask in pending)
        {
            try
            {
                var workspace = await cloneTask.ConfigureAwait(false);
                await workspace.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The clone was cancelled by scope teardown, failed, or was already
                // cleaned up by the factory; nothing left to dispose here.
                this.logger.LogDebug(ex, "Shared workspace clone disposal skipped a cancelled or faulted clone task");
            }
        }

        this.scopeCts.Dispose();
    }
}
