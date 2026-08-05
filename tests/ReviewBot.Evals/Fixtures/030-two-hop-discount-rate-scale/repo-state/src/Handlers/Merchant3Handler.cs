namespace Scale.Handlers;

using Scale.Services;

public sealed class Merchant3Handler
{
    private readonly Merchant3Service service;

    public Merchant3Handler(Merchant3Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
