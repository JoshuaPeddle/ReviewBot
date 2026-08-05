namespace Scale.Handlers;

using Scale.Services;

public sealed class Refund2Handler
{
    private readonly Refund2Service service;

    public Refund2Handler(Refund2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
