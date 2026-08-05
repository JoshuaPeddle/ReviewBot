namespace Scale.Handlers;

using Scale.Services;

public sealed class Voucher1Handler
{
    private readonly Voucher1Service service;

    public Voucher1Handler(Voucher1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
