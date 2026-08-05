namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Tenant2Service
{
    private readonly Tenant2Repository repository;

    public Tenant2Service(Tenant2Repository repository) => this.repository = repository;

    public Tenant2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
