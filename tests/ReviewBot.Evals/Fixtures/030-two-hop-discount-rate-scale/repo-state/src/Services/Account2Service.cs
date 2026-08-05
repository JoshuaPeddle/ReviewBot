namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Account2Service
{
    private readonly Account2Repository repository;

    public Account2Service(Account2Repository repository) => this.repository = repository;

    public Account2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
