namespace Scale.Handlers;

using Scale.Services;

public sealed class Ledger3Handler
{
    private readonly Ledger3Service service;

    public Ledger3Handler(Ledger3Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
