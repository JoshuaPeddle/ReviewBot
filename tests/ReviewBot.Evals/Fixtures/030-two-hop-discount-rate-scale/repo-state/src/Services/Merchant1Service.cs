namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Merchant1Service
{
    private readonly Merchant1Repository repository;

    public Merchant1Service(Merchant1Repository repository) => this.repository = repository;

    public Merchant1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
