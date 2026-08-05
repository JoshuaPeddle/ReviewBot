namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Campaign2Service
{
    private readonly Campaign2Repository repository;

    public Campaign2Service(Campaign2Repository repository) => this.repository = repository;

    public Campaign2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
