namespace Scale.Handlers;

using Scale.Services;

public sealed class Payment2Handler
{
    private readonly Payment2Service service;

    public Payment2Handler(Payment2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
