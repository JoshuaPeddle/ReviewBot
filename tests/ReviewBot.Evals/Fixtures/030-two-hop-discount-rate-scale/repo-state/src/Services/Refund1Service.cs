namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Refund1Service
{
    private readonly Refund1Repository repository;

    public Refund1Service(Refund1Repository repository) => this.repository = repository;

    public Refund1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
