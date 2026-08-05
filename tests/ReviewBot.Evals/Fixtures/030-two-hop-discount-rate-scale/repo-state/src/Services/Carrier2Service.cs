namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Carrier2Service
{
    private readonly Carrier2Repository repository;

    public Carrier2Service(Carrier2Repository repository) => this.repository = repository;

    public Carrier2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
