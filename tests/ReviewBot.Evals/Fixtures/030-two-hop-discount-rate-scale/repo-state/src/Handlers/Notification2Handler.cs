namespace Scale.Handlers;

using Scale.Services;

public sealed class Notification2Handler
{
    private readonly Notification2Service service;

    public Notification2Handler(Notification2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
