namespace Scale.Handlers;

using Scale.Services;

public sealed class Carrier2Handler
{
    private readonly Carrier2Service service;

    public Carrier2Handler(Carrier2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
