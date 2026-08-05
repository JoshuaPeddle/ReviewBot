namespace Scale.Handlers;

using Scale.Services;

public sealed class CheckoutHandler
{
    private readonly OrderService orders;

    public CheckoutHandler(OrderService orders) => this.orders = orders;

    public decimal Checkout(decimal total, string promoCode)
    {
        var rate = this.orders.RateFor(promoCode);
        var percent = rate * 100m;
        return this.orders.ApplyDiscount(total, percent);
    }
}
