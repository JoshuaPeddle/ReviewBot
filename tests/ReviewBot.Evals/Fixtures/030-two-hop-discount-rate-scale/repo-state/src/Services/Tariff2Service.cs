namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Tariff2Service
{
    private readonly Tariff2Repository repository;

    public Tariff2Service(Tariff2Repository repository) => this.repository = repository;

    public Tariff2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
