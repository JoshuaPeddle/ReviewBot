using Microsoft.Extensions.DependencyInjection;
using ReviewBot.Grounding.Build;
using ReviewBot.Grounding.Detection;
using ReviewBot.Grounding.Workspace;

namespace ReviewBot.Grounding;

public static class GroundingServiceCollectionExtensions
{
    public static GroundingBuilder AddGrounding(this IServiceCollection services)
    {
        services.AddSingleton<IWorkspaceFactory, GitWorkspaceFactory>();
        services.AddSingleton<IGroundingProvider, CompositeGroundingProvider>();
        return new GroundingBuilder(services);
    }
}

public sealed class GroundingBuilder
{
    private readonly IServiceCollection services;

    internal GroundingBuilder(IServiceCollection services) => this.services = services;

    public GroundingBuilder AddLanguageDetector<T>() where T : class, ILanguageDetector
    {
        services.AddSingleton<ILanguageDetector, T>();
        return this;
    }

    public GroundingBuilder AddBuildRunner<T>() where T : class, IBuildRunner
    {
        services.AddSingleton<IBuildRunner, T>();
        return this;
    }
}
