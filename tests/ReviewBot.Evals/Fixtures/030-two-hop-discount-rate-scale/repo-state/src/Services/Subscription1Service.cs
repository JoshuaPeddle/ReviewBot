namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Subscription1Service
{
    private readonly Subscription1Repository repository;

    public Subscription1Service(Subscription1Repository repository) => this.repository = repository;

    public Subscription1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
