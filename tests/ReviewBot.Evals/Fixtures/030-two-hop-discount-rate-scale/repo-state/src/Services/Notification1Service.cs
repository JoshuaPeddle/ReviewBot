namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Notification1Service
{
    private readonly Notification1Repository repository;

    public Notification1Service(Notification1Repository repository) => this.repository = repository;

    public Notification1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
