using FluentAssertions;
using ReviewBot.Grounding.Build;

namespace ReviewBot.Grounding.Tests.Build;

public class ProcessCommandTests
{
    [Fact]
    public void TryParse_SplitsCommandWithoutUsingShell()
    {
        var ok = ProcessCommand.TryParse(
            """python3 -c "print('hello world')" 'two words' escaped\ space""",
            out var command,
            out var error);

        ok.Should().BeTrue(error);
        command.Should().NotBeNull();
        command!.FileName.Should().Be("python3");
        command.Arguments.Should().Equal("-c", "print('hello world')", "two words", "escaped space");
    }

    [Fact]
    public void TryParse_UnterminatedQuote_ReturnsError()
    {
        var ok = ProcessCommand.TryParse("""python3 -c "print(1)""", out var command, out var error);

        ok.Should().BeFalse();
        command.Should().BeNull();
        error.Should().Contain("unterminated quote");
    }

    [Fact]
    public void TryParse_PreservesBackslashesThatAreNotEscapingShellSyntax()
    {
        var ok = ProcessCommand.TryParse("""tool --regex \d+\s+value""", out var command, out var error);

        ok.Should().BeTrue(error);
        command!.Arguments.Should().Equal("--regex", @"\d+\s+value");
    }
}
