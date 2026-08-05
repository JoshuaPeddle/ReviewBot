namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Campaign3Service
{
    private readonly Campaign3Repository repository;

    public Campaign3Service(Campaign3Repository repository) => this.repository = repository;

    public Campaign3Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
