namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Listing2Service
{
    private readonly Listing2Repository repository;

    public Listing2Service(Listing2Repository repository) => this.repository = repository;

    public Listing2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
