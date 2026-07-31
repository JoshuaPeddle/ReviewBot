namespace ReviewBot.Core.Context;

public interface IModelContextRegistry
{
    int GetContextWindowTokens(string modelIdentifier);

    int ApplyConfiguredCap(string modelIdentifier, int discoveredTokens);
}
