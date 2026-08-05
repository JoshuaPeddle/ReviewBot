namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Account3Service
{
    private readonly Account3Repository repository;

    public Account3Service(Account3Repository repository) => this.repository = repository;

    public Account3Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
