namespace Scale.Handlers;

using Scale.Services;

public sealed class Catalog1Handler
{
    private readonly Catalog1Service service;

    public Catalog1Handler(Catalog1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
