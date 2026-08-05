namespace Scale.Handlers;

using Scale.Services;

public sealed class Pricing1Handler
{
    private readonly Pricing1Service service;

    public Pricing1Handler(Pricing1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
