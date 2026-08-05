namespace Scale.Handlers;

using Scale.Services;

public sealed class Merchant2Handler
{
    private readonly Merchant2Service service;

    public Merchant2Handler(Merchant2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
