namespace Scale.Handlers;

using Scale.Services;

public sealed class Listing3Handler
{
    private readonly Listing3Service service;

    public Listing3Handler(Listing3Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
