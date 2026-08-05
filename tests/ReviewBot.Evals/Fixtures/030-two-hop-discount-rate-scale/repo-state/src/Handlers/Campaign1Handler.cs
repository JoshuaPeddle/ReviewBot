namespace Scale.Handlers;

using Scale.Services;

public sealed class Campaign1Handler
{
    private readonly Campaign1Service service;

    public Campaign1Handler(Campaign1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
