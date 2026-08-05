namespace Scale.Handlers;

using Scale.Services;

public sealed class Supplier2Handler
{
    private readonly Supplier2Service service;

    public Supplier2Handler(Supplier2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
