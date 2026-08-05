namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Address2Service
{
    private readonly Address2Repository repository;

    public Address2Service(Address2Repository repository) => this.repository = repository;

    public Address2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
