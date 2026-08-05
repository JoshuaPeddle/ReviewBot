namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Manifest3Service
{
    private readonly Manifest3Repository repository;

    public Manifest3Service(Manifest3Repository repository) => this.repository = repository;

    public Manifest3Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
