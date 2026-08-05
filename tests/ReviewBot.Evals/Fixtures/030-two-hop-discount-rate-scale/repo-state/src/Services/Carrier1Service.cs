namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Carrier1Service
{
    private readonly Carrier1Repository repository;

    public Carrier1Service(Carrier1Repository repository) => this.repository = repository;

    public Carrier1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
