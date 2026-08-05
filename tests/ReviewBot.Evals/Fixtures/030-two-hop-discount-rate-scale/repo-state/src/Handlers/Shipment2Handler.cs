namespace Scale.Handlers;

using Scale.Services;

public sealed class Shipment2Handler
{
    private readonly Shipment2Service service;

    public Shipment2Handler(Shipment2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
