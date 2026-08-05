namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Delivery3Service
{
    private readonly Delivery3Repository repository;

    public Delivery3Service(Delivery3Repository repository) => this.repository = repository;

    public Delivery3Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
