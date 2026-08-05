namespace Scale.Handlers;

using Scale.Services;

public sealed class Basket2Handler
{
    private readonly Basket2Service service;

    public Basket2Handler(Basket2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
