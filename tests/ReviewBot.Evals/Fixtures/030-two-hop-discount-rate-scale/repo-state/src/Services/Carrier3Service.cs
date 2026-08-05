namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Carrier3Service
{
    private readonly Carrier3Repository repository;

    public Carrier3Service(Carrier3Repository repository) => this.repository = repository;

    public Carrier3Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
