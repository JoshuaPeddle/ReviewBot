namespace Scale.Handlers;

using Scale.Services;

public sealed class Fulfilment1Handler
{
    private readonly Fulfilment1Service service;

    public Fulfilment1Handler(Fulfilment1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
