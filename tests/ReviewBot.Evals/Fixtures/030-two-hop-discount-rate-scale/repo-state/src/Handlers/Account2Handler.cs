namespace Scale.Handlers;

using Scale.Services;

public sealed class Account2Handler
{
    private readonly Account2Service service;

    public Account2Handler(Account2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
