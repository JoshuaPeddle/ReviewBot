namespace Scale.Handlers;

using Scale.Services;

public sealed class Account3Handler
{
    private readonly Account3Service service;

    public Account3Handler(Account3Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
