namespace Scale.Handlers;

using Scale.Services;

public sealed class Subscription2Handler
{
    private readonly Subscription2Service service;

    public Subscription2Handler(Subscription2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
