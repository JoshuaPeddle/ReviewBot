using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ReviewBot.Core.Context;

public static class DependencyInjection
{
    public static IServiceCollection AddPromptBudgeting(
        this IServiceCollection services,
        Action<ModelContextOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ModelContextOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<IModelContextRegistry, ModelContextRegistry>();
        services.TryAddSingleton<IPromptTokenEstimator, HeuristicTokenEstimator>();
        services.TryAddSingleton<IReviewPromptTokenEstimator, ReviewPromptTokenEstimator>();

        return services;
    }
}
