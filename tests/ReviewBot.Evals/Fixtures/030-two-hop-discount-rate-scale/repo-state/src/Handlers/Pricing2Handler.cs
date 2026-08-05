namespace Scale.Handlers;

using Scale.Services;

public sealed class Pricing2Handler
{
    private readonly Pricing2Service service;

    public Pricing2Handler(Pricing2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
