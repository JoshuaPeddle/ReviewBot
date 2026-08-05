namespace Scale.Handlers;

using Scale.Services;

public sealed class Delivery3Handler
{
    private readonly Delivery3Service service;

    public Delivery3Handler(Delivery3Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
