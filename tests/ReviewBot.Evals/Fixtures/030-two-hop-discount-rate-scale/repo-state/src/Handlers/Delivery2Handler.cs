namespace Scale.Handlers;

using Scale.Services;

public sealed class Delivery2Handler
{
    private readonly Delivery2Service service;

    public Delivery2Handler(Delivery2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
