namespace Scale.Handlers;

using Scale.Services;

public sealed class Channel3Handler
{
    private readonly Channel3Service service;

    public Channel3Handler(Channel3Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
