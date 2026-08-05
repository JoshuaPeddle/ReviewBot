namespace Scale.Handlers;

using Scale.Services;

public sealed class Address1Handler
{
    private readonly Address1Service service;

    public Address1Handler(Address1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
