namespace ReviewBot.Grounding.Workspace;

public interface IWorkspaceFactory
{
    Task<IWorkspace> CreateAsync(WorkspaceRequest request, CancellationToken ct);
}
