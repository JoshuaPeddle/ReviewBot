namespace Scale.Handlers;

using Scale.Services;

public sealed class Tenant2Handler
{
    private readonly Tenant2Service service;

    public Tenant2Handler(Tenant2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
