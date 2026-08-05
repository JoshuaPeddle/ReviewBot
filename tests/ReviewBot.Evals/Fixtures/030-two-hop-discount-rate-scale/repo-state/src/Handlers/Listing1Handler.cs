namespace Scale.Handlers;

using Scale.Services;

public sealed class Listing1Handler
{
    private readonly Listing1Service service;

    public Listing1Handler(Listing1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
