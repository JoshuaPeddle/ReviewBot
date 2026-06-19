using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Llm;

public interface IReviewLlmFactory
{
    IReviewLlm Create(ModelConfig modelConfig);

    // The effective model name for the config: the configured name when present, otherwise the
    // selected provider's own configured model (from host options / environment variables).
    string ResolveModelName(ModelConfig modelConfig);
}
