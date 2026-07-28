namespace ReviewBot.Core.Llm;

public sealed record ReviewLlmProviderRegistration
{
    public ReviewLlmProviderRegistration(
        string providerName,
        Func<IServiceProvider, IConfigurableReviewLlm> resolve)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(resolve);

        ProviderName = providerName;
        Resolve = resolve;
    }

    public string ProviderName { get; }

    public Func<IServiceProvider, IConfigurableReviewLlm> Resolve { get; }
}
