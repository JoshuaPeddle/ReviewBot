namespace Scale.Handlers;

using Scale.Services;

public sealed class Invoice3Handler
{
    private readonly Invoice3Service service;

    public Invoice3Handler(Invoice3Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
