namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Tenant1Service
{
    private readonly Tenant1Repository repository;

    public Tenant1Service(Tenant1Repository repository) => this.repository = repository;

    public Tenant1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
