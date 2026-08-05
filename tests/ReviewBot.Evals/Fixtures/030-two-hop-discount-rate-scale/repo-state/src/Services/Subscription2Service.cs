namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Subscription2Service
{
    private readonly Subscription2Repository repository;

    public Subscription2Service(Subscription2Repository repository) => this.repository = repository;

    public Subscription2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
