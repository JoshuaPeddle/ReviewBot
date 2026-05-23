using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Llm;

public interface IReviewLlmFactory
{
    IReviewLlm Create(ModelConfig modelConfig);
}
