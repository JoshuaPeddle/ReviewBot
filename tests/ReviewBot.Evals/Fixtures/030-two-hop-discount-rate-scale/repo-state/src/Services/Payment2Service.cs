namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Payment2Service
{
    private readonly Payment2Repository repository;

    public Payment2Service(Payment2Repository repository) => this.repository = repository;

    public Payment2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
