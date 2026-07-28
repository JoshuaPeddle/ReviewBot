namespace ReviewBot.Core.Llm;

public interface IConfigurableReviewLlm : IReviewLlm
{
    string ProviderName { get; }

    // The model the provider is currently configured to call. When the repo config omits a model
    // name, the factory falls back to this (provider-configured, e.g. REVIEWBOT__OpenAi__ModelName).
    string ModelName { get; }

    // Providers clone their existing dependencies with a new model name so the Core factory stays provider-agnostic.
    IReviewLlm WithModelName(string modelName);
}
