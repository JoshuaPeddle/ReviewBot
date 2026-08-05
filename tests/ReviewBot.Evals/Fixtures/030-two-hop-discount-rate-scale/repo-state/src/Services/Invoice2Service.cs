namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Invoice2Service
{
    private readonly Invoice2Repository repository;

    public Invoice2Service(Invoice2Repository repository) => this.repository = repository;

    public Invoice2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
