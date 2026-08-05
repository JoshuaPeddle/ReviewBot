namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Tax2Service
{
    private readonly Tax2Repository repository;

    public Tax2Service(Tax2Repository repository) => this.repository = repository;

    public Tax2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
