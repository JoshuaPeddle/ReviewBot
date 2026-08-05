namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Address3Service
{
    private readonly Address3Repository repository;

    public Address3Service(Address3Repository repository) => this.repository = repository;

    public Address3Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
