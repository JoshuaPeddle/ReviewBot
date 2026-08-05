namespace Scale.Handlers;

using Scale.Services;

public sealed class Address3Handler
{
    private readonly Address3Service service;

    public Address3Handler(Address3Service service) => this.service = service;

    public decimal Summarise() => this.service.TotalAmount();
}
