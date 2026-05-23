namespace ReviewBot.Grounding.Workspace;

public interface IWorkspace : IAsyncDisposable
{
    string LocalPath { get; }
}
