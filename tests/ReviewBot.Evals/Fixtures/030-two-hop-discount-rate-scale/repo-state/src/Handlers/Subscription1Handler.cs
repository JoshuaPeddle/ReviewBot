namespace Scale.Handlers;

using Scale.Services;

public sealed class Subscription1Handler
{
    private readonly Subscription1Service service;

    public Subscription1Handler(Subscription1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
