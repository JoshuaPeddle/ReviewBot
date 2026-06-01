using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReviewBot.Core.Context;
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
        services.TryAddSingleton<IPromptTokenEstimator, HeuristicTokenEstimator>();
        services.AddSingleton<AnthropicReviewLlm>();
        services.AddSingleton<IProviderPromptTokenEstimator, AnthropicTokenEstimator>();
        services.AddSingleton(new ReviewLlmProviderRegistration(
            "anthropic",
            provider => provider.GetRequiredService<AnthropicReviewLlm>()));
        services.AddSingleton<IConfigurableReviewLlm>(provider => provider.GetRequiredService<AnthropicReviewLlm>());
        services.AddSingleton<IReviewLlm>(provider => provider.GetRequiredService<AnthropicReviewLlm>());

        return services;
    }
}
