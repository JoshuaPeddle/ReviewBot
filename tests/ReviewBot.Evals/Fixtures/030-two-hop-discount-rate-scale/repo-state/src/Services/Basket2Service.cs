namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Basket2Service
{
    private readonly Basket2Repository repository;

    public Basket2Service(Basket2Repository repository) => this.repository = repository;

    public Basket2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
