namespace Scale.Handlers;

using Scale.Services;

public sealed class Campaign2Handler
{
    private readonly Campaign2Service service;

    public Campaign2Handler(Campaign2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
