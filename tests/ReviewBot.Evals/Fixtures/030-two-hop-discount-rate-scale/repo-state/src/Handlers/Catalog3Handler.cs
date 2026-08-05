namespace Scale.Handlers;

using Scale.Services;

public sealed class Catalog3Handler
{
    private readonly Catalog3Service service;

    public Catalog3Handler(Catalog3Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
