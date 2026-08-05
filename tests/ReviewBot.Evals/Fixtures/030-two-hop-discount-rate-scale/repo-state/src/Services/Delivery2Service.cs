namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Delivery2Service
{
    private readonly Delivery2Repository repository;

    public Delivery2Service(Delivery2Repository repository) => this.repository = repository;

    public Delivery2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
