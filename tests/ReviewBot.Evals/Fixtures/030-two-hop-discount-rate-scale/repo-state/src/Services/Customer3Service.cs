namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Customer3Service
{
    private readonly Customer3Repository repository;

    public Customer3Service(Customer3Repository repository) => this.repository = repository;

    public Customer3Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
