using Microsoft.Extensions.DependencyInjection;
using ReviewBot.Core.Llm;

namespace ReviewBot.Llm.OpenAi;

public static class DependencyInjection
{
    public static IServiceCollection AddOpenAiReviewLlm(
        this IServiceCollection services,
        Action<OpenAiLlmOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new OpenAiLlmOptions();
        configure(options);

        services.AddSingleton(options);
        services.AddSingleton<OpenAiReviewLlm>();
        services.AddSingleton(new ReviewLlmProviderRegistration(
            "openai",
            provider => provider.GetRequiredService<OpenAiReviewLlm>()));
        services.AddSingleton<IConfigurableReviewLlm>(provider => provider.GetRequiredService<OpenAiReviewLlm>());
        services.AddSingleton<IReviewLlm>(provider => provider.GetRequiredService<OpenAiReviewLlm>());

        return services;
    }
}
