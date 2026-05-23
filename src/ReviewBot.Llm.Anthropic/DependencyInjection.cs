using Microsoft.Extensions.DependencyInjection;
using ReviewBot.Core.Llm;

namespace ReviewBot.Llm.Anthropic;

public static class DependencyInjection
{
    public static IServiceCollection AddAnthropicReviewLlm(
        this IServiceCollection services,
        Action<AnthropicLlmOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new AnthropicLlmOptions();
        configure(options);

        services.AddSingleton(options);
        services.AddSingleton<AnthropicReviewLlm>();
        services.AddSingleton<IReviewLlm>(provider => provider.GetRequiredService<AnthropicReviewLlm>());

        return services;
    }
}
