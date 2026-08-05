namespace Scale.Handlers;

using Scale.Services;

public sealed class Listing2Handler
{
    private readonly Listing2Service service;

    public Listing2Handler(Listing2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
