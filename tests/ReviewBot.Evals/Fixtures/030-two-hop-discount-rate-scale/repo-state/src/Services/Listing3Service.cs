namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Listing3Service
{
    private readonly Listing3Repository repository;

    public Listing3Service(Listing3Repository repository) => this.repository = repository;

    public Listing3Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
