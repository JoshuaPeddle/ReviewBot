namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Basket1Service
{
    private readonly Basket1Repository repository;

    public Basket1Service(Basket1Repository repository) => this.repository = repository;

    public Basket1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
