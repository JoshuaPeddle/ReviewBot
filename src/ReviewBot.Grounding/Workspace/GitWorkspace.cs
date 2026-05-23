namespace ReviewBot.Grounding.Workspace;

internal sealed class GitWorkspace : IWorkspace
{
    public string LocalPath { get; }

    internal GitWorkspace(string localPath) => LocalPath = localPath;

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(LocalPath))
            Directory.Delete(LocalPath, recursive: true);
        return ValueTask.CompletedTask;
    }
}
