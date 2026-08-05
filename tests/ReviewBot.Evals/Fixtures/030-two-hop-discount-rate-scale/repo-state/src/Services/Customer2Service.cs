namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Customer2Service
{
    private readonly Customer2Repository repository;

    public Customer2Service(Customer2Repository repository) => this.repository = repository;

    public Customer2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
