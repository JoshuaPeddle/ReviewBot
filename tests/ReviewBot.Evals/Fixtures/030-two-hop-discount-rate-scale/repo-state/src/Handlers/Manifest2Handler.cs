namespace Scale.Handlers;

using Scale.Services;

public sealed class Manifest2Handler
{
    private readonly Manifest2Service service;

    public Manifest2Handler(Manifest2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
