using Microsoft.Extensions.DependencyInjection;
using ReviewBot.Grounding.Detection;

namespace ReviewBot.Grounding;

public static class GroundingServiceCollectionExtensions
{
    public static GroundingBuilder AddGrounding(this IServiceCollection services)
    {
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
}
