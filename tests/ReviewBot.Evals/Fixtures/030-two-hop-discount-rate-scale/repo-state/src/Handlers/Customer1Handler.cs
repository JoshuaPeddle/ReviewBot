namespace Scale.Handlers;

using Scale.Services;

public sealed class Customer1Handler
{
    private readonly Customer1Service service;

    public Customer1Handler(Customer1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
