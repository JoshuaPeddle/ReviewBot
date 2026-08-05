namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Refund2Service
{
    private readonly Refund2Repository repository;

    public Refund2Service(Refund2Repository repository) => this.repository = repository;

    public Refund2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
