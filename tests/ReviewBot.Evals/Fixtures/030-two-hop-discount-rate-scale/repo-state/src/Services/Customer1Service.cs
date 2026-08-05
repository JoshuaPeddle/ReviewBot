namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Customer1Service
{
    private readonly Customer1Repository repository;

    public Customer1Service(Customer1Repository repository) => this.repository = repository;

    public Customer1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
