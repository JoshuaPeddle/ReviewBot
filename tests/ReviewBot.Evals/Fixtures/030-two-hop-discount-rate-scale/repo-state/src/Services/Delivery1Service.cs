namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Delivery1Service
{
    private readonly Delivery1Repository repository;

    public Delivery1Service(Delivery1Repository repository) => this.repository = repository;

    public Delivery1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
