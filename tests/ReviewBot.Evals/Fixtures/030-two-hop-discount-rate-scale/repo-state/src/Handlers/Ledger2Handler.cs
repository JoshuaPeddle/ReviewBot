namespace Scale.Handlers;

using Scale.Services;

public sealed class Ledger2Handler
{
    private readonly Ledger2Service service;

    public Ledger2Handler(Ledger2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
