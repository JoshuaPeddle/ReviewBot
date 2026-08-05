namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Address1Service
{
    private readonly Address1Repository repository;

    public Address1Service(Address1Repository repository) => this.repository = repository;

    public Address1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
