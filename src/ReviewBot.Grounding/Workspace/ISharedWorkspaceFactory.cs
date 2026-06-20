namespace ReviewBot.Grounding.Workspace;

/// <summary>
/// Creates a fresh <see cref="ISharedWorkspace"/> scope for a single review
/// job. Registered as a singleton; each job creates and disposes its own scope,
/// so clones are never shared across concurrent jobs (each job may mutate its
/// workspace, e.g. when verifying a proposed fix).
/// </summary>
public interface ISharedWorkspaceFactory
{
    ISharedWorkspace Create();
}
