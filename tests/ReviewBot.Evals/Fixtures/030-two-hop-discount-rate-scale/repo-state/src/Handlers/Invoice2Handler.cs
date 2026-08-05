namespace Scale.Handlers;

using Scale.Services;

public sealed class Invoice2Handler
{
    private readonly Invoice2Service service;

    public Invoice2Handler(Invoice2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
