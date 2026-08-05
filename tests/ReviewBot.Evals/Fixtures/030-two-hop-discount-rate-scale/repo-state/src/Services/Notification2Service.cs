namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Notification2Service
{
    private readonly Notification2Repository repository;

    public Notification2Service(Notification2Repository repository) => this.repository = repository;

    public Notification2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
