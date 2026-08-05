namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Merchant2Service
{
    private readonly Merchant2Repository repository;

    public Merchant2Service(Merchant2Repository repository) => this.repository = repository;

    public Merchant2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
