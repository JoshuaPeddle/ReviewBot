namespace ReviewBot.E2E.Tests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class E2eCollection : ICollectionFixture<ReviewBotHarness>
{
    public const string Name = "E2E";
}
