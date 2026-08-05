namespace Scale.Handlers;

using Scale.Services;

public sealed class Tax1Handler
{
    private readonly Tax1Service service;

    public Tax1Handler(Tax1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
