namespace Scale.Handlers;

using Scale.Services;

public sealed class Warehouse1Handler
{
    private readonly Warehouse1Service service;

    public Warehouse1Handler(Warehouse1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
