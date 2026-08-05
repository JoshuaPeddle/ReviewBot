namespace Scale.Handlers;

using Scale.Services;

public sealed class Delivery1Handler
{
    private readonly Delivery1Service service;

    public Delivery1Handler(Delivery1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
