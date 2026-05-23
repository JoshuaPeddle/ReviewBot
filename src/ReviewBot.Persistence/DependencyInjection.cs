using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReviewBot.Core.Idempotency;
using ReviewBot.Core.Storage;

namespace ReviewBot.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddReviewBotPersistence(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContextFactory<ReviewBotDbContext>(configure);
        services.AddSingleton<IDeliveryStore, EfCoreDeliveryStore>();
        services.AddSingleton<IPrReviewStateStore, EfCorePrReviewStateStore>();

        return services;
    }
}
