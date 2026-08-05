namespace Scale.Handlers;

using Scale.Services;

public sealed class Fulfilment2Handler
{
    private readonly Fulfilment2Service service;

    public Fulfilment2Handler(Fulfilment2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
