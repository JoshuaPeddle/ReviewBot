namespace Scale.Handlers;

using Scale.Services;

public sealed class Merchant1Handler
{
    private readonly Merchant1Service service;

    public Merchant1Handler(Merchant1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
