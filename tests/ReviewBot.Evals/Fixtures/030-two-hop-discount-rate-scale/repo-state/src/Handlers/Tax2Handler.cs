namespace Scale.Handlers;

using Scale.Services;

public sealed class Tax2Handler
{
    private readonly Tax2Service service;

    public Tax2Handler(Tax2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
