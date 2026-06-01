using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ReviewBot.Retrieval.Indexing;
using ReviewBot.Retrieval.Symbols;

namespace ReviewBot.Retrieval;

public static class DependencyInjection
{
    public static IServiceCollection AddRetrieval(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IDiffSymbolExtractor, CSharpDiffSymbolExtractor>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRepoSymbolParser, CSharpRepoSymbolParser>());
        services.TryAddSingleton<IRepoIndexFactory, SqliteRepoIndexFactory>();
        services.TryAddSingleton<IRetrievalProvider, SqliteRetrievalProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RepoIndexCleanupService>());

        return services;
    }
}
