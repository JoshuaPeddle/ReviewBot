namespace Scale.Handlers;

using Scale.Services;

public sealed class Reservation1Handler
{
    private readonly Reservation1Service service;

    public Reservation1Handler(Reservation1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
