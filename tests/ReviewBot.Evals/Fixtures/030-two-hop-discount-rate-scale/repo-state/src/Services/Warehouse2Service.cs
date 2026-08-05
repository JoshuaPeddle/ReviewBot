namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Warehouse2Service
{
    private readonly Warehouse2Repository repository;

    public Warehouse2Service(Warehouse2Repository repository) => this.repository = repository;

    public Warehouse2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
