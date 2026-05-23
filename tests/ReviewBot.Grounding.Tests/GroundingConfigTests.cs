using FluentAssertions;
using ReviewBot.Core.Domain;

namespace ReviewBot.Grounding.Tests;

public class GroundingConfigTests
{
    [Fact]
    public void DefaultGroundingConfigHasExpectedValues()
    {
        var d = GroundingConfig.Default;

        d.Enabled.Should().BeTrue();
        d.Build.Should().BeFalse();
        d.Tests.Should().BeFalse();
        d.BuildTimeoutSeconds.Should().Be(120);
        d.TestTimeoutSeconds.Should().Be(300);
        d.BuildCommand.Should().BeNull();
        d.TestCommand.Should().BeNull();
    }

    [Fact]
    public void ReviewConfigDefaultIncludesGroundingDefault()
    {
        ReviewConfig.Default.Grounding.Should().BeEquivalentTo(GroundingConfig.Default);
    }

    [Fact]
    public void GroundingContextWithAllNullsIsValid()
    {
        var ctx = new GroundingContext(null, null, null);

        ctx.Language.Should().BeNull();
        ctx.Build.Should().BeNull();
        ctx.Tests.Should().BeNull();
    }
}
