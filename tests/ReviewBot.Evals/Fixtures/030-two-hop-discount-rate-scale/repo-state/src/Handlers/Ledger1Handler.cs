namespace Scale.Handlers;

using Scale.Services;

public sealed class Ledger1Handler
{
    private readonly Ledger1Service service;

    public Ledger1Handler(Ledger1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
