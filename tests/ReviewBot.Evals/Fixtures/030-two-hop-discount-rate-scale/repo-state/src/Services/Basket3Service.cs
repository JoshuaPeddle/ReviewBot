namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Basket3Service
{
    private readonly Basket3Repository repository;

    public Basket3Service(Basket3Repository repository) => this.repository = repository;

    public Basket3Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
