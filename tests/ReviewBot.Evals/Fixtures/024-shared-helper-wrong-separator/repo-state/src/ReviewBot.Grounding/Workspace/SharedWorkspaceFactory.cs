using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReviewBot.Grounding.Workspace;

public sealed class SharedWorkspaceFactory : ISharedWorkspaceFactory
{
    private readonly IWorkspaceFactory factory;
    private readonly ILogger<SharedWorkspaceFactory> logger;

    public SharedWorkspaceFactory(
        IWorkspaceFactory factory,
        ILogger<SharedWorkspaceFactory>? logger = null)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.logger = logger ?? NullLogger<SharedWorkspaceFactory>.Instance;
    }

    public ISharedWorkspace Create() => new SharedWorkspace(this.factory, this.logger);
}
