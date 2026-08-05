namespace Scale.Handlers;

using Scale.Services;

public sealed class Warehouse2Handler
{
    private readonly Warehouse2Service service;

    public Warehouse2Handler(Warehouse2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
