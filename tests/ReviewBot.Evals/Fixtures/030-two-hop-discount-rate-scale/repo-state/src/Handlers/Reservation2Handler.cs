namespace Scale.Handlers;

using Scale.Services;

public sealed class Reservation2Handler
{
    private readonly Reservation2Service service;

    public Reservation2Handler(Reservation2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
