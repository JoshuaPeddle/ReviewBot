using Microsoft.Extensions.DependencyInjection;
using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Llm;

public sealed class ReviewLlmFactory : IReviewLlmFactory
{
    private readonly IServiceProvider provider;

    public ReviewLlmFactory(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        this.provider = provider;
    }

    public IReviewLlm Create(ModelConfig modelConfig)
    {
        ArgumentNullException.ThrowIfNull(modelConfig);

        var registrations = provider
            .GetServices<ReviewLlmProviderRegistration>()
            .ToArray();
        var registration = registrations.FirstOrDefault(candidate =>
            string.Equals(candidate.ProviderName, modelConfig.Provider, StringComparison.OrdinalIgnoreCase));

        if (registration is null)
        {
            var supportedProviders = registrations
                .Select(candidate => candidate.ProviderName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var supportedProviderText = supportedProviders.Length == 0
                ? "none registered"
                : string.Join(", ", supportedProviders);

            throw new InvalidOperationException(
                $"Unsupported LLM provider '{modelConfig.Provider}'. Supported providers: {supportedProviderText}.");
        }

        var llm = registration.Resolve(provider);
        return llm.WithModelName(modelConfig.Name);
    }
}
