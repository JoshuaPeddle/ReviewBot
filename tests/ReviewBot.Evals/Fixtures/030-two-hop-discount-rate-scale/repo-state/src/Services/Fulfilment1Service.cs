namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Fulfilment1Service
{
    private readonly Fulfilment1Repository repository;

    public Fulfilment1Service(Fulfilment1Repository repository) => this.repository = repository;

    public Fulfilment1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
