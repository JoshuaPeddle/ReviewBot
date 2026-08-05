namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Shipment2Service
{
    private readonly Shipment2Repository repository;

    public Shipment2Service(Shipment2Repository repository) => this.repository = repository;

    public Shipment2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
