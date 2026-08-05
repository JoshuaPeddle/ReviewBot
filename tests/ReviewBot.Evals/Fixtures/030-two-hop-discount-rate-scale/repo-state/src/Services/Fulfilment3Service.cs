namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Fulfilment3Service
{
    private readonly Fulfilment3Repository repository;

    public Fulfilment3Service(Fulfilment3Repository repository) => this.repository = repository;

    public Fulfilment3Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
