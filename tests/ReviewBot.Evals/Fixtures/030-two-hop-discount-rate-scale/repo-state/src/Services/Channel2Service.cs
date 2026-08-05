namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Channel2Service
{
    private readonly Channel2Repository repository;

    public Channel2Service(Channel2Repository repository) => this.repository = repository;

    public Channel2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
