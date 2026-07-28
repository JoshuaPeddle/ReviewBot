namespace ReviewBot.Grounding.Workspace;

/// <summary>
/// A job-scoped holder for one or more cloned workspaces. Multiple consumers
/// within a single review job (grounding, retrieval indexing, finding
/// verification) call <see cref="GetOrCreateAsync"/> and share a single clone
/// per <c>(CloneUrl, Sha)</c>; the underlying workspace is cloned lazily on
/// first request and disposed exactly once when the scope is disposed at the
/// end of the job. Workspaces returned from <see cref="GetOrCreateAsync"/> are
/// owned by the scope — callers must not dispose them.
/// </summary>
public interface ISharedWorkspace : IAsyncDisposable
{
    Task<IWorkspace> GetOrCreateAsync(WorkspaceRequest request, CancellationToken ct);
}
