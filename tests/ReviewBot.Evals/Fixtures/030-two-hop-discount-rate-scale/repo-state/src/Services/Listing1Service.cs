namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Listing1Service
{
    private readonly Listing1Repository repository;

    public Listing1Service(Listing1Repository repository) => this.repository = repository;

    public Listing1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
