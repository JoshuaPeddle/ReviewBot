namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Manifest2Service
{
    private readonly Manifest2Repository repository;

    public Manifest2Service(Manifest2Repository repository) => this.repository = repository;

    public Manifest2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
