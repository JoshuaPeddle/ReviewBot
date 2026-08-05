namespace Scale.Handlers;

using Scale.Services;

public sealed class Address2Handler
{
    private readonly Address2Service service;

    public Address2Handler(Address2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
