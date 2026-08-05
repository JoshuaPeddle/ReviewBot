namespace Scale.Handlers;

using Scale.Services;

public sealed class Tariff2Handler
{
    private readonly Tariff2Service service;

    public Tariff2Handler(Tariff2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
