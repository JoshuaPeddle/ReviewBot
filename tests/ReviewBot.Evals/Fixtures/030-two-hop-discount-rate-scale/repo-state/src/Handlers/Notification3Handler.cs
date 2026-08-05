namespace Scale.Handlers;

using Scale.Services;

public sealed class Notification3Handler
{
    private readonly Notification3Service service;

    public Notification3Handler(Notification3Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
