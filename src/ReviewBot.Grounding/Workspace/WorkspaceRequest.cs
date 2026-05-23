namespace ReviewBot.Grounding.Workspace;

public sealed record WorkspaceRequest(
    string CloneUrl,
    string Sha,
    string InstallationToken);
