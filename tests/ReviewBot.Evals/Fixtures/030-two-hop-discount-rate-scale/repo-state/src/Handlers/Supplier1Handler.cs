namespace Scale.Handlers;

using Scale.Services;

public sealed class Supplier1Handler
{
    private readonly Supplier1Service service;

    public Supplier1Handler(Supplier1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
