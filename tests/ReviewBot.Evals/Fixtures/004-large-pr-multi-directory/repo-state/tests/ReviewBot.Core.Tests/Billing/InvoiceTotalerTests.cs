using FluentAssertions;
using ReviewBot.Core.Billing;

namespace ReviewBot.Core.Tests.Billing;

public sealed class InvoiceTotalerTests
{
    public void TotalSubtractsDiscount()
    {
        new InvoiceTotaler().Total(100m, 15m).Should().Be(85m);
    }
}
