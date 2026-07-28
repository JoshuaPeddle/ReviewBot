namespace ReviewBot.Core.Context;

public interface IModelContextRegistry
{
    int GetContextWindowTokens(string modelIdentifier);
}
