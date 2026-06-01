namespace ReviewBot.Core.Billing;

public sealed class InvoiceTotaler
{
    public decimal Total(decimal subtotal, decimal discount)
    {
        if (discount < 0) throw new ArgumentOutOfRangeException(nameof(discount));
        return subtotal - discount;
    }
}
