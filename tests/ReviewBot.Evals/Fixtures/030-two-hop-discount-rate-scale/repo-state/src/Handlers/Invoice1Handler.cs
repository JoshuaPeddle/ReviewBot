namespace Scale.Handlers;

using Scale.Services;

public sealed class Invoice1Handler
{
    private readonly Invoice1Service service;

    public Invoice1Handler(Invoice1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
