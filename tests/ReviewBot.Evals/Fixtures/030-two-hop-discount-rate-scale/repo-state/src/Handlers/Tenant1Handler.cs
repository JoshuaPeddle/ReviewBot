namespace Scale.Handlers;

using Scale.Services;

public sealed class Tenant1Handler
{
    private readonly Tenant1Service service;

    public Tenant1Handler(Tenant1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
