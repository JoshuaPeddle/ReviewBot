namespace Scale.Handlers;

using Scale.Services;

public sealed class Channel1Handler
{
    private readonly Channel1Service service;

    public Channel1Handler(Channel1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
