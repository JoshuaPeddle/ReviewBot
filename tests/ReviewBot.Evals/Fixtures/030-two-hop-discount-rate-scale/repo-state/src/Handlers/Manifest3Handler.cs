namespace Scale.Handlers;

using Scale.Services;

public sealed class Manifest3Handler
{
    private readonly Manifest3Service service;

    public Manifest3Handler(Manifest3Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
