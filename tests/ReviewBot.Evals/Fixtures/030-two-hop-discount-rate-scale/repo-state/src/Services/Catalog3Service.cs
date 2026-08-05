namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Catalog3Service
{
    private readonly Catalog3Repository repository;

    public Catalog3Service(Catalog3Repository repository) => this.repository = repository;

    public Catalog3Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
