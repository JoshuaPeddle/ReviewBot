namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Supplier1Service
{
    private readonly Supplier1Repository repository;

    public Supplier1Service(Supplier1Repository repository) => this.repository = repository;

    public Supplier1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
