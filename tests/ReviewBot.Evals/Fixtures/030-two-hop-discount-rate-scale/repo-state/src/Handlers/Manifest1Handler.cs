namespace Scale.Handlers;

using Scale.Services;

public sealed class Manifest1Handler
{
    private readonly Manifest1Service service;

    public Manifest1Handler(Manifest1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
