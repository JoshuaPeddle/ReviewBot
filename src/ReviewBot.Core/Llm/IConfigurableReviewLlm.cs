namespace ReviewBot.Core.Llm;

public interface IConfigurableReviewLlm : IReviewLlm
{
    string ProviderName { get; }

    // Providers clone their existing dependencies with a new model name so the Core factory stays provider-agnostic.
    IReviewLlm WithModelName(string modelName);
}
