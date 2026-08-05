namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Pricing2Service
{
    private readonly Pricing2Repository repository;

    public Pricing2Service(Pricing2Repository repository) => this.repository = repository;

    public Pricing2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
