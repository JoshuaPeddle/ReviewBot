namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Manifest1Service
{
    private readonly Manifest1Repository repository;

    public Manifest1Service(Manifest1Repository repository) => this.repository = repository;

    public Manifest1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
