namespace Scale.Handlers;

using Scale.Services;

public sealed class Shipment1Handler
{
    private readonly Shipment1Service service;

    public Shipment1Handler(Shipment1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
