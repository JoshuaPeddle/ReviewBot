namespace Scale.Handlers;

using Scale.Services;

public sealed class Tariff1Handler
{
    private readonly Tariff1Service service;

    public Tariff1Handler(Tariff1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
