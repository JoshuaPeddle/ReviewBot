namespace Scale.Handlers;

using Scale.Services;

public sealed class Catalog2Handler
{
    private readonly Catalog2Service service;

    public Catalog2Handler(Catalog2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
