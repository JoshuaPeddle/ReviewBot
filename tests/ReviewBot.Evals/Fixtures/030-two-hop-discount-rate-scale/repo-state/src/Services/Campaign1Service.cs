namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Campaign1Service
{
    private readonly Campaign1Repository repository;

    public Campaign1Service(Campaign1Repository repository) => this.repository = repository;

    public Campaign1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
