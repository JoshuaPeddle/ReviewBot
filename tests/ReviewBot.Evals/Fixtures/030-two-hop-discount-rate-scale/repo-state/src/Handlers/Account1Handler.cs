namespace Scale.Handlers;

using Scale.Services;

public sealed class Account1Handler
{
    private readonly Account1Service service;

    public Account1Handler(Account1Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
