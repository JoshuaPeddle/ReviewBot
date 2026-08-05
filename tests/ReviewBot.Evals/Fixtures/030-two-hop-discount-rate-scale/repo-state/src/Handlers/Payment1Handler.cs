namespace Scale.Handlers;

using Scale.Services;

public sealed class Payment1Handler
{
    private readonly Payment1Service service;

    public Payment1Handler(Payment1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
