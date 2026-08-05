namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Fulfilment2Service
{
    private readonly Fulfilment2Repository repository;

    public Fulfilment2Service(Fulfilment2Repository repository) => this.repository = repository;

    public Fulfilment2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
