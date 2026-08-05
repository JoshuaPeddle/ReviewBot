namespace Scale.Handlers;

using Scale.Services;

public sealed class Refund1Handler
{
    private readonly Refund1Service service;

    public Refund1Handler(Refund1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
