namespace Scale.Handlers;

using Scale.Services;

public sealed class Basket1Handler
{
    private readonly Basket1Service service;

    public Basket1Handler(Basket1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
