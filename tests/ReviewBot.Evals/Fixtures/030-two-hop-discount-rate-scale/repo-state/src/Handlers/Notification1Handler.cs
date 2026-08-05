namespace Scale.Handlers;

using Scale.Services;

public sealed class Notification1Handler
{
    private readonly Notification1Service service;

    public Notification1Handler(Notification1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
