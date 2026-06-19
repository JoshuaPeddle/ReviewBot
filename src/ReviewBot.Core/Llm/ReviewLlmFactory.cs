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

        var llm = GetRegistration(modelConfig.Provider).Resolve(provider);

        // An empty name means the repo config did not pin a model; keep the provider's own
        // configured model (e.g. REVIEWBOT__OpenAi__ModelName) rather than overriding it.
        return string.IsNullOrWhiteSpace(modelConfig.Name)
            ? llm
            : llm.WithModelName(modelConfig.Name);
    }

    public string ResolveModelName(ModelConfig modelConfig)
    {
        ArgumentNullException.ThrowIfNull(modelConfig);

        return string.IsNullOrWhiteSpace(modelConfig.Name)
            ? GetRegistration(modelConfig.Provider).Resolve(provider).ModelName
            : modelConfig.Name;
    }

    private ReviewLlmProviderRegistration GetRegistration(string providerName)
    {
        var registrations = provider
            .GetServices<ReviewLlmProviderRegistration>()
            .ToArray();
        var registration = registrations.FirstOrDefault(candidate =>
            string.Equals(candidate.ProviderName, providerName, StringComparison.OrdinalIgnoreCase));

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
                $"Unsupported LLM provider '{providerName}'. Supported providers: {supportedProviderText}.");
        }

        return registration;
    }
}
