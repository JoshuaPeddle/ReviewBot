using Microsoft.Extensions.DependencyInjection;

namespace ReviewBot.Core.Jobs;

public static class DependencyInjection
{
    public static IServiceCollection AddChannelReviewJobQueue(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IReviewJobQueue, ChannelReviewJobQueue>();
        services.AddSingleton<ChannelReviewJobQueue>(provider =>
            (ChannelReviewJobQueue)provider.GetRequiredService<IReviewJobQueue>());

        return services;
    }
}
