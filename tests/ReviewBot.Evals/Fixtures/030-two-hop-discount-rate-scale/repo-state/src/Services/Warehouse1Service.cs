namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Warehouse1Service
{
    private readonly Warehouse1Repository repository;

    public Warehouse1Service(Warehouse1Repository repository) => this.repository = repository;

    public Warehouse1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
