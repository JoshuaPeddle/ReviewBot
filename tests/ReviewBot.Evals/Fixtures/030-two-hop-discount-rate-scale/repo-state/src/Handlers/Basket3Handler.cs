namespace Scale.Handlers;

using Scale.Services;

public sealed class Basket3Handler
{
    private readonly Basket3Service service;

    public Basket3Handler(Basket3Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
