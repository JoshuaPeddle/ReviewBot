namespace Scale.Handlers;

using Scale.Services;

public sealed class Campaign3Handler
{
    private readonly Campaign3Service service;

    public Campaign3Handler(Campaign3Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
