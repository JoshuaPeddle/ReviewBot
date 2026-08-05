namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Catalog2Service
{
    private readonly Catalog2Repository repository;

    public Catalog2Service(Catalog2Repository repository) => this.repository = repository;

    public Catalog2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
