namespace Scale.Services;

using Scale.Repositories;

public sealed class OrderService
{
    private readonly DiscountRepository discounts;

    public OrderService(DiscountRepository discounts) => this.discounts = discounts;

    public decimal RateFor(string code) => this.discounts.GetRate(code);

    /// <summary>
    /// Subtracts <paramref name="rate"/> of <paramref name="total"/>.
    /// </summary>
    /// <param name="rate">
    /// A <b>fraction</b>, matching <see cref="DiscountRepository.GetRate"/> — 0.15 is 15% off.
    /// Passing 15 here discounts 1500% and returns a large negative total.
    /// </param>
    public decimal ApplyDiscount(decimal total, decimal rate) => total - (total * rate);
}
