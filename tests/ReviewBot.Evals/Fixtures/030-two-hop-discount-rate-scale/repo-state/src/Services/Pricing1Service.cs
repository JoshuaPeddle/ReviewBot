namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Pricing1Service
{
    private readonly Pricing1Repository repository;

    public Pricing1Service(Pricing1Repository repository) => this.repository = repository;

    public Pricing1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
