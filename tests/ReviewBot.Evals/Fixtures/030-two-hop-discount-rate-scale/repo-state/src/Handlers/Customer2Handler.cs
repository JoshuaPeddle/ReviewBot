namespace Scale.Handlers;

using Scale.Services;

public sealed class Customer2Handler
{
    private readonly Customer2Service service;

    public Customer2Handler(Customer2Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
