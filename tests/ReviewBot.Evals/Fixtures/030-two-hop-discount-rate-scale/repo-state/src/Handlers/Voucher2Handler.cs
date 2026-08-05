namespace Scale.Handlers;

using Scale.Services;

public sealed class Voucher2Handler
{
    private readonly Voucher2Service service;

    public Voucher2Handler(Voucher2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
