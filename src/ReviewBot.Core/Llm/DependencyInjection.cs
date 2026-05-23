using Microsoft.Extensions.DependencyInjection;

namespace ReviewBot.Core.Llm;

public static class DependencyInjection
{
    public static IServiceCollection AddReviewLlmFactory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IReviewLlmFactory, ReviewLlmFactory>();
        services.AddSingleton<ReviewLlmFactory>(provider =>
            (ReviewLlmFactory)provider.GetRequiredService<IReviewLlmFactory>());

        return services;
    }
}
