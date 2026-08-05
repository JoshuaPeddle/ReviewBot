namespace Scale.Handlers;

using Scale.Services;

public sealed class Fulfilment3Handler
{
    private readonly Fulfilment3Service service;

    public Fulfilment3Handler(Fulfilment3Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
