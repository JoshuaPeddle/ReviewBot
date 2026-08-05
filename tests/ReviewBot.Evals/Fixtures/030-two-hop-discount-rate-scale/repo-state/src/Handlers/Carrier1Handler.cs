namespace Scale.Handlers;

using Scale.Services;

public sealed class Carrier1Handler
{
    private readonly Carrier1Service service;

    public Carrier1Handler(Carrier1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
