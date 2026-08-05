namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Invoice1Service
{
    private readonly Invoice1Repository repository;

    public Invoice1Service(Invoice1Repository repository) => this.repository = repository;

    public Invoice1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
