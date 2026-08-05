namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Account1Service
{
    private readonly Account1Repository repository;

    public Account1Service(Account1Repository repository) => this.repository = repository;

    public Account1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
