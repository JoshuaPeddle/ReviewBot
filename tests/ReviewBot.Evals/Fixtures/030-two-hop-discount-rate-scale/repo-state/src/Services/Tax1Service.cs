namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Tax1Service
{
    private readonly Tax1Repository repository;

    public Tax1Service(Tax1Repository repository) => this.repository = repository;

    public Tax1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
