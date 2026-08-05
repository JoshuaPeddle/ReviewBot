namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Tariff1Service
{
    private readonly Tariff1Repository repository;

    public Tariff1Service(Tariff1Repository repository) => this.repository = repository;

    public Tariff1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
