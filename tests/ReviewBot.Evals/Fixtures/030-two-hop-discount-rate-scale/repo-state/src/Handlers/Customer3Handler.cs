namespace Scale.Handlers;

using Scale.Services;

public sealed class Customer3Handler
{
    private readonly Customer3Service service;

    public Customer3Handler(Customer3Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
