namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Shipment1Service
{
    private readonly Shipment1Repository repository;

    public Shipment1Service(Shipment1Repository repository) => this.repository = repository;

    public Shipment1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
