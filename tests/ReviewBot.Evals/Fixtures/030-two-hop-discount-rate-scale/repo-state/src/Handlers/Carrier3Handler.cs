namespace Scale.Handlers;

using Scale.Services;

public sealed class Carrier3Handler
{
    private readonly Carrier3Service service;

    public Carrier3Handler(Carrier3Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
