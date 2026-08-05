namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Catalog1Service
{
    private readonly Catalog1Repository repository;

    public Catalog1Service(Catalog1Repository repository) => this.repository = repository;

    public Catalog1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
