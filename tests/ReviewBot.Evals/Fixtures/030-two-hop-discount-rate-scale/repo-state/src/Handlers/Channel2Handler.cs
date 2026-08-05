namespace Scale.Handlers;

using Scale.Services;

public sealed class Channel2Handler
{
    private readonly Channel2Service service;

    public Channel2Handler(Channel2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
