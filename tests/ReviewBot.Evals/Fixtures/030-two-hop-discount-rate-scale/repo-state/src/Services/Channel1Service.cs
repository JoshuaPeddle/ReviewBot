namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Channel1Service
{
    private readonly Channel1Repository repository;

    public Channel1Service(Channel1Repository repository) => this.repository = repository;

    public Channel1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
